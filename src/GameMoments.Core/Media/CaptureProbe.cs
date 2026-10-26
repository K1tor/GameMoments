using GameMoments.Core.Configuration;
using GameMoments.Core.Logging;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Media;

public sealed record CaptureProbeResult(bool Success, CaptureProfile Profile, string Detail, TimeSpan Elapsed);

/// <summary>
/// 启动前逐条真实探测候选管线（编码 0.6 秒到 null），只有真正跑通的组合才会被采用。
/// </summary>
public static class CaptureProbe
{
    public static IReadOnlyList<CaptureProfile> Candidates(FfmpegCapabilities capabilities, AppSettings settings)
    {
        var sources = new List<CaptureSource>();
        var preference = settings.CaptureSourcePreference;
        if (preference is "auto" or "ddagrab" && capabilities.Ddagrab) sources.Add(CaptureSource.Ddagrab);
        if (preference is "auto" or "gdigrab" && capabilities.Gdigrab) sources.Add(CaptureSource.Gdigrab);
        if (sources.Count == 0)
        {
            if (capabilities.Ddagrab) sources.Add(CaptureSource.Ddagrab);
            if (capabilities.Gdigrab) sources.Add(CaptureSource.Gdigrab);
        }

        var hardware = new List<string>();
        if (capabilities.Nvenc) hardware.Add("h264_nvenc");
        if (capabilities.Amf) hardware.Add("h264_amf");
        if (capabilities.Qsv) hardware.Add("h264_qsv");
        if (capabilities.MediaFoundation) hardware.Add("h264_mf");

        var encoders = new List<string>();
        if (settings.EncoderPreference is "auto" or "hardware")
        {
            encoders.AddRange(hardware);
            if (capabilities.Libx264) encoders.Add("libx264");
        }
        else
        {
            if (capabilities.Libx264) encoders.Add("libx264");
            encoders.AddRange(hardware);
        }

        if (encoders.Count == 0)
        {
            if (capabilities.Libx264) encoders.Add("libx264");
            if (capabilities.Nvenc) encoders.Add("h264_nvenc");
        }

        var fps = settings.Fps == 30 ? 30 : 60;
        var result = new List<CaptureProfile>();
        foreach (var source in sources)
        {
            foreach (var encoder in encoders)
            {
                result.Add(new CaptureProfile(source, encoder, fps));
            }
        }
        return result;
    }

    public static CaptureProbeResult Probe(FfmpegTools tools, CaptureProfile profile, DisplayInfo display, AppLogger logger, int timeoutMs = 20_000)
    {
        var started = DateTime.UtcNow;
        var arguments = FfmpegCommands.Probe(profile, display);
        var result = ProcessRunner.Run(tools.FfmpegPath, arguments, timeoutMs);

        if (result.Success)
        {
            var elapsed = DateTime.UtcNow - started;
            logger.Info("捕获探测", $"通过：{profile.DisplayName}（{display.Label}）", $"耗时 {elapsed.TotalMilliseconds:0} ms");
            return new CaptureProbeResult(true, profile, "OK", elapsed);
        }

        var detail = result.TimedOut
            ? $"探测超时（{timeoutMs} ms）"
            : $"退出码 {result.ExitCode}：{result.OutputTail(700)}";
        logger.Warn("捕获探测", $"失败：{profile.DisplayName}", detail);
        return new CaptureProbeResult(false, profile, detail, DateTime.UtcNow - started);
    }

    /// <summary>按候选顺序探测，返回第一个可用的组合与全部失败详情。</summary>
    public static CaptureProfile? Select(
        FfmpegTools tools,
        FfmpegCapabilities capabilities,
        AppSettings settings,
        DisplayInfo display,
        AppLogger logger,
        out string failureDetail,
        CancellationToken cancellationToken = default)
    {
        var details = new List<string>();
        foreach (var profile in Candidates(capabilities, settings))
        {
            if (cancellationToken.IsCancellationRequested) break;
            var outcome = Probe(tools, profile, display, logger);
            if (outcome.Success) { failureDetail = string.Empty; return outcome.Profile; }
            details.Add($"{profile.DisplayName} → {outcome.Detail}");
        }

        failureDetail = details.Count == 0
            ? "没有可用的捕获组合（ffmpeg 缺少 gdigrab/ddagrab 或编码器）。"
            : string.Join(Environment.NewLine, details);
        logger.Error("捕获探测", "所有捕获组合均失败", null, failureDetail);
        return null;
    }
}
