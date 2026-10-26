using System.Diagnostics;
using System.Text;

namespace GameMoments.Core.Media;

/// <summary>
/// 长时间运行的 ffmpeg 进程封装：解析 -progress 输出、保留 stderr 尾部、支持发送 q 优雅退出。
/// </summary>
public sealed class FfmpegProcess : IAsyncDisposable
{
    private const int MaxErrorTail = 16_000;

    private readonly Process _process;
    private readonly StringBuilder _errorTail = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource<int> _exitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<string>? ProgressLine;
    public event Action<string>? ErrorLine;

    public int ProcessId { get; }

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch
            {
                return true;
            }
        }
    }

    public string ErrorTail
    {
        get
        {
            lock (_gate) return _errorTail.ToString();
        }
    }

    public Task<int> WaitForExitAsync() => _exitSource.Task;

    private FfmpegProcess(Process process)
    {
        _process = process;
        ProcessId = process.Id;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            try
            {
                ProgressLine?.Invoke(e.Data);
            }
            catch
            {
                // 订阅者异常忽略。
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_gate)
            {
                _errorTail.AppendLine(e.Data);
                if (_errorTail.Length > MaxErrorTail)
                {
                    _errorTail.Remove(0, _errorTail.Length - MaxErrorTail);
                }
            }
            try
            {
                ErrorLine?.Invoke(e.Data);
            }
            catch
            {
                // 订阅者异常忽略。
            }
        };
        process.Exited += (_, _) =>
        {
            var code = -1;
            try
            {
                code = process.ExitCode;
            }
            catch
            {
                // 进程句柄可能已关闭。
            }
            _exitSource.TrySetResult(code);
        };
    }

    public static FfmpegProcess Start(string ffmpegPath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("ffmpeg 进程启动失败。");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"ffmpeg 进程启动失败：{ex.Message}", ex);
        }

        var wrapper = new FfmpegProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return wrapper;
    }

    /// <summary>发送 q 让 ffmpeg 正常收尾（写完当前分段），超时后强制结束进程树。</summary>
    public async Task<bool> StopGracefullyAsync(TimeSpan timeout)
    {
        if (HasExited) return true;

        try
        {
            await _process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // stdin 已关闭。
        }

        var completed = await Task.WhenAny(_exitSource.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed == _exitSource.Task) return true;

        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 忽略。
        }

        await Task.WhenAny(_exitSource.Task, Task.Delay(3000)).ConfigureAwait(false);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 忽略。
        }
        await Task.WhenAny(_exitSource.Task, Task.Delay(2000)).ConfigureAwait(false);
        _process.Dispose();
    }
}
