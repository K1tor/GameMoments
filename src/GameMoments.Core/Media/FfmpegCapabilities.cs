using GameMoments.Core.Logging;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Media;

public sealed class FfmpegCapabilities
{
    public bool Ddagrab { get; private set; }
    public bool Gdigrab { get; private set; }
    public bool Nvenc { get; private set; }
    public bool Amf { get; private set; }
    public bool Qsv { get; private set; }
    public bool MediaFoundation { get; private set; }
    public bool Libx264 { get; private set; }

    public bool HasHardwareEncoder => Nvenc || Amf || Qsv || MediaFoundation;

    public static FfmpegCapabilities Detect(FfmpegTools tools, AppLogger logger, int timeoutMs = 30_000)
    {
        var capabilities = new FfmpegCapabilities();
        var encoders = ProcessRunner.Run(tools.FfmpegPath, ["-hide_banner", "-encoders"], timeoutMs);
        var filters = ProcessRunner.Run(tools.FfmpegPath, ["-hide_banner", "-filters"], timeoutMs);
        var devices = ProcessRunner.Run(tools.FfmpegPath, ["-hide_banner", "-devices"], timeoutMs);

        if (!encoders.Success)
        {
            logger.Warn("FFmpeg", "无法读取编码器列表", encoders.OutputTail(500));
        }

        capabilities.Nvenc = Contains(encoders.StandardOutput, "h264_nvenc");
        capabilities.Amf = Contains(encoders.StandardOutput, "h264_amf");
        capabilities.Qsv = Contains(encoders.StandardOutput, "h264_qsv");
        capabilities.MediaFoundation = Contains(encoders.StandardOutput, "h264_mf");
        capabilities.Libx264 = Contains(encoders.StandardOutput, "libx264");

        capabilities.Ddagrab = Contains(filters.StandardOutput, "ddagrab");
        capabilities.Gdigrab = Contains(devices.StandardOutput, "gdigrab");

        logger.Info("FFmpeg", "能力检测完成",
            $"ddagrab={capabilities.Ddagrab}, gdigrab={capabilities.Gdigrab}, nvenc={capabilities.Nvenc}, " +
            $"amf={capabilities.Amf}, qsv={capabilities.Qsv}, mf={capabilities.MediaFoundation}, libx264={capabilities.Libx264}");
        return capabilities;
    }

    public string Summary()
    {
        var parts = new List<string>();
        if (Ddagrab) parts.Add("桌面复制");
        if (Gdigrab) parts.Add("GDI 捕获");
        var encoders = new List<string>();
        if (Nvenc) encoders.Add("NVENC");
        if (Amf) encoders.Add("AMF");
        if (Qsv) encoders.Add("QSV");
        if (MediaFoundation) encoders.Add("MediaFoundation");
        if (Libx264) encoders.Add("x264");
        return $"{string.Join(" / ", parts)}；编码：{string.Join(" / ", encoders)}";
    }

    private static bool Contains(string text, string token) =>
        text.Contains(token, StringComparison.OrdinalIgnoreCase);
}
