using GameMoments.Core.Logging;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Media;

/// <summary>定位并验证 ffmpeg / ffprobe。</summary>
public sealed class FfmpegTools
{
    public string FfmpegPath { get; }
    public string FfprobePath { get; }
    public string Version { get; }
    public string Banner { get; }

    public string DirectoryPath => Path.GetDirectoryName(FfmpegPath) ?? string.Empty;

    public FfmpegTools(string ffmpegPath, string ffprobePath, string banner)
    {
        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath;
        Banner = banner;
        var firstLine = banner.Split('\n').FirstOrDefault() ?? banner;
        Version = firstLine.Trim();
    }

    public static FfmpegTools? Locate(string? configuredPath, string? dataDirectory, AppLogger logger, out string error)
    {
        error = string.Empty;
        var tried = new List<string>();

        foreach (var candidate in Candidates(configuredPath, dataDirectory))
        {
            if (!File.Exists(candidate)) continue;
            tried.Add(candidate);
            var version = ProcessRunner.Run(candidate, ["-hide_banner", "-version"], 15_000);
            var banner = version.StandardOutput;
            if (!version.Success || !banner.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            {
                logger.Warn("FFmpeg", "候选 ffmpeg 无法运行", $"{candidate}: {version.OutputTail(600)}");
                continue;
            }

            var probePath = FindFfprobe(candidate);
            if (probePath is null)
            {
                error = $"找到了 ffmpeg（{candidate}），但缺少同目录的 ffprobe.exe。请使用包含 ffprobe 的完整构建。";
                logger.Error("FFmpeg", error);
                return null;
            }

            var probeVersion = ProcessRunner.Run(probePath, ["-hide_banner", "-version"], 15_000);
            if (!probeVersion.Success || !probeVersion.StandardOutput.Contains("ffprobe version", StringComparison.OrdinalIgnoreCase))
            {
                error = $"ffprobe 无法运行：{probePath}。{probeVersion.OutputTail(300)}";
                logger.Error("FFmpeg", error);
                return null;
            }

            var tools = new FfmpegTools(Path.GetFullPath(candidate), Path.GetFullPath(probePath), banner);
            logger.Info("FFmpeg", $"使用 ffmpeg：{tools.FfmpegPath}", tools.Version);
            return tools;
        }

        error = "未找到可用的 ffmpeg 与 ffprobe。请将 ffmpeg 放在程序目录的 ffmpeg\\ 子目录，或在设置中指定 ffmpeg.exe 路径。"
                + (tried.Count > 0 ? $"（尝试过：{string.Join("；", tried)}）" : string.Empty);
        logger.Error("FFmpeg", error);
        return null;
    }

    public static IEnumerable<string> Candidates(string? configuredPath, string? dataDirectory)
    {
        var results = new List<string>();
        void AddFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path.Trim().Trim('"'));
                results.Add(full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, "ffmpeg.exe"));
            }
            catch
            {
                // 忽略非法路径。
            }
        }

        AddFile(configuredPath);

        var env = Environment.GetEnvironmentVariable(AppPaths.FfmpegEnvVar);
        AddFile(env);
        AddFile(Environment.GetEnvironmentVariable("GAMEMOMENTS_FFMPEG"));

        var baseDir = AppContext.BaseDirectory;
        AddFile(Path.Combine(baseDir, "ffmpeg"));
        AddFile(baseDir);
        AddFile(Path.Combine(baseDir, "tools", "ffmpeg"));
        AddFile(Path.Combine(baseDir, "..", "..", "..", "..", "tools", "ffmpeg"));
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            AddFile(Path.Combine(dataDirectory, "ffmpeg"));
        }

        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                AddFile(Path.Combine(dir.FullName, "tools", "ffmpeg"));
                AddFile(Path.Combine(dir.FullName, "ffmpeg"));
                dir = dir.Parent;
            }
        }
        catch
        {
            // 忽略。
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddFile(entry);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            AddFile(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            AddFile(Path.Combine(local, "ffmpeg", "bin"));
        }
        AddFile(@"C:\ffmpeg\bin");
        AddFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"));

        return results.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindFfprobe(string ffmpegPath)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (dir is null) return null;
        var candidate = Path.Combine(dir, "ffprobe.exe");
        return File.Exists(candidate) ? candidate : null;
    }
}
