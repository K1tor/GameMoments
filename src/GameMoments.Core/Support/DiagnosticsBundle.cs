using System.IO.Compression;
using System.Text;
using GameMoments.Core.Audio;
using GameMoments.Core.Media;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Support;

/// <summary>
/// 生成诊断包：系统信息、设置、最近的日志与 ffmpeg 信息。全部本地生成，不上传。
/// </summary>
public static class DiagnosticsBundle
{
    public static string Create(
        string outputPath,
        AppPaths paths,
        string appVersion,
        FfmpegTools? tools,
        FfmpegCapabilities? capabilities,
        string? lastError)
    {
        var tempRoot = Path.Combine(paths.TempDirectory, "diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "system.txt"), BuildSystemInfo(paths, appVersion, tools, capabilities, lastError), Encoding.UTF8);

            if (File.Exists(paths.SettingsFile))
            {
                File.Copy(paths.SettingsFile, Path.Combine(tempRoot, "settings.json"), overwrite: true);
            }

            var logTarget = Path.Combine(tempRoot, "logs");
            Directory.CreateDirectory(logTarget);
            if (Directory.Exists(paths.LogDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(paths.LogDirectory, "app-*.jsonl")
                             .OrderByDescending(f => f)
                             .Take(5))
                {
                    File.Copy(file, Path.Combine(logTarget, Path.GetFileName(file)), overwrite: true);
                }
            }

            File.WriteAllText(Path.Combine(tempRoot, "说明.txt"),
                "此诊断包由 GameMoments 在本地生成，仅包含系统信息、设置、最近日志与 ffmpeg 能力，可用于排查问题。\r\n" +
                "内容不会自动上传；请自行确认后再分享。\r\n", Encoding.UTF8);

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            ZipFile.CreateFromDirectory(tempRoot, outputPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return outputPath;
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响诊断包。
            }
        }
    }

    private static string BuildSystemInfo(
        AppPaths paths,
        string appVersion,
        FfmpegTools? tools,
        FfmpegCapabilities? capabilities,
        string? lastError)
    {
        var sb = new StringBuilder();
        sb.AppendLine("GameMoments 诊断信息");
        sb.AppendLine("====================");
        sb.AppendLine($"应用版本    : {appVersion}");
        sb.AppendLine($"生成时间    : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"操作系统    : {Environment.OSVersion}");
        sb.AppendLine($"64 位系统   : {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"处理器      : {Environment.ProcessorCount} 逻辑核心");
        sb.AppendLine($"运行框架    : {Environment.Version}");
        sb.AppendLine($"数据目录    : {Privacy.Redact(paths.DataDirectory)}");
        sb.AppendLine($"保存目录    : {Privacy.Redact(paths.DefaultSaveDirectory)}");
        sb.AppendLine();

        sb.AppendLine("显示器");
        sb.AppendLine("------");
        try
        {
            foreach (var display in Displays.Enumerate())
            {
                sb.AppendLine($"#{display.Index} {display.DeviceName} {display.SizeText} 主屏={display.IsPrimary}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("枚举失败：" + ex.Message);
        }
        sb.AppendLine();

        sb.AppendLine("音频设备");
        sb.AppendLine("--------");
        AppendDevices(sb, AudioDeviceKind.Render, "回放");
        AppendDevices(sb, AudioDeviceKind.Capture, "录制");
        sb.AppendLine();

        sb.AppendLine("ffmpeg");
        sb.AppendLine("------");
        sb.AppendLine(tools is null ? "未找到 ffmpeg" : $"路径: {tools.FfmpegPath}");
        sb.AppendLine(tools is null ? string.Empty : $"版本: {tools.Version}");
        if (capabilities is not null)
        {
            sb.AppendLine($"ddagrab={capabilities.Ddagrab}, gdigrab={capabilities.Gdigrab}, nvenc={capabilities.Nvenc}, " +
                          $"amf={capabilities.Amf}, qsv={capabilities.Qsv}, mf={capabilities.MediaFoundation}, libx264={capabilities.Libx264}");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(lastError))
        {
            sb.AppendLine("最近错误");
            sb.AppendLine("--------");
            sb.AppendLine(lastError);
        }

        return sb.ToString();
    }

    private static void AppendDevices(StringBuilder sb, AudioDeviceKind kind, string label)
    {
        sb.AppendLine($"[{label}]");
        try
        {
            var devices = AudioDeviceService.Enumerate(kind);
            if (devices.Count == 0)
            {
                sb.AppendLine("(无)");
                return;
            }
            foreach (var device in devices)
            {
                sb.AppendLine($"{(device.IsDefault ? "*" : " ")} {device.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("枚举失败：" + ex.Message);
        }
    }
}
