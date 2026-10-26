using System.Diagnostics;
using System.Text;

namespace GameMoments.Core.Platform;

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    TimeSpan Elapsed,
    string? StartError = null)
{
    public bool Success => !TimedOut && StartError is null && ExitCode == 0;

    public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();

    public string OutputTail(int maxChars = 4000)
    {
        var text = CombinedOutput;
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}

/// <summary>同步运行外部进程并收集 stdout/stderr，用于 ffmpeg/ffprobe 的短命令探测。</summary>
public static class ProcessRunner
{
    public static ProcessResult Run(
        string fileName,
        IEnumerable<string> arguments,
        int timeoutMs = 20_000,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
                             ?? Path.GetDirectoryName(fileName)
                             ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var (key, value) in environment) psi.Environment[key] = value;
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false, stopwatch.Elapsed, ex.GetType().Name);
        }

        if (process is null)
        {
            return new ProcessResult(-1, string.Empty, "进程未能启动。", false, stopwatch.Elapsed, "ProcessStartFailed");
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // 忽略：部分程序不读取 stdin。
            }

            var exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                TryKill(process);
            }

            var stdout = SafeResult(stdoutTask);
            var stderr = SafeResult(stderrTask);
            var exitCode = -1;
            if (exited)
            {
                try
                {
                    exitCode = process.ExitCode;
                }
                catch
                {
                    exitCode = -1;
                }
            }

            return new ProcessResult(exitCode, stdout, stderr, !exited, stopwatch.Elapsed);
        }
    }

    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.Wait(3000) ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能刚好退出。
        }
    }
}
