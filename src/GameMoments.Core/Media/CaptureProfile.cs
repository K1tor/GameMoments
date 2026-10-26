namespace GameMoments.Core.Media;

public enum CaptureSource
{
    Ddagrab,
    Gdigrab,
}

/// <summary>一条“捕获方式 + 编码器”的组合，启动前会逐条真实探测。</summary>
public sealed record CaptureProfile(CaptureSource Source, string Encoder, int Fps)
{
    public bool HardwareEncoder => Encoder is "h264_nvenc" or "h264_amf" or "h264_qsv" or "h264_mf";

    public string SourceName => Source == CaptureSource.Ddagrab ? "桌面复制（GPU）" : "GDI 捕获（CPU）";

    public string EncoderName => Encoder switch
    {
        "h264_nvenc" => "NVENC 硬件编码",
        "h264_amf" => "AMF 硬件编码",
        "h264_qsv" => "QSV 硬件编码",
        "h264_mf" => "Media Foundation 编码",
        "libx264" => "x264 软件编码",
        _ => Encoder,
    };

    public string DisplayName => $"{SourceName} + {EncoderName}";

    public string ShortName => Encoder switch
    {
        "h264_nvenc" => "NVENC",
        "h264_amf" => "AMF",
        "h264_qsv" => "QSV",
        "h264_mf" => "MF",
        "libx264" => "x264",
        _ => Encoder,
    };

    public string Id => $"{Source.ToString().ToLowerInvariant()}:{Encoder}:{Fps}";
}
