using System.Globalization;
using System.Text;
using GameMoments.Core.Configuration;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Media;

public sealed record AudioInput(string Path, float Volume);

/// <summary>集中构造 ffmpeg 命令行参数，便于单元测试与调整。</summary>
public static class FfmpegCommands
{
    public static List<string> Record(CaptureProfile profile, AppSettings settings, DisplayInfo display, string segmentDirectory)
    {
        var args = Global();
        args.AddRange(Input(profile, display));
        if (profile.Source == CaptureSource.Ddagrab && profile.Encoder != "h264_nvenc")
        {
            args.AddRange(["-vf", "hwdownload,format=bgra"]);
        }
        args.AddRange("-an -sn -dn".Split(' '));
        args.AddRange(Encoder(profile, settings));
        args.AddRange(["-fps_mode", "cfr", "-r", profile.Fps.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange([
            "-f", "segment",
            "-segment_time", "1",
            "-segment_format", "mp4",
            "-reset_timestamps", "1",
            Path.Combine(segmentDirectory, "seg_%05d.mp4"),
        ]);
        return args;
    }

    public static List<string> Probe(CaptureProfile profile, DisplayInfo display)
    {
        var args = Global();
        args.AddRange(Input(profile, display));
        if (profile.Source == CaptureSource.Ddagrab && profile.Encoder != "h264_nvenc")
        {
            args.AddRange(["-vf", "hwdownload,format=bgra"]);
        }
        args.AddRange(Encoder(profile, new AppSettings { Fps = profile.Fps, QualityPreset = "medium" }));
        args.AddRange(["-t", "0.6", "-f", "null", "-"]);
        return args;
    }

    public static List<string> Concat(string listFile, string output)
    {
        var args = Global();
        args.AddRange(["-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", "-movflags", "+faststart", output]);
        return args;
    }

    public static List<string> Mux(string videoPath, IReadOnlyList<AudioInput> audioInputs, string output, string? title = null)
    {
        var args = Global();
        args.AddRange(["-i", videoPath]);
        foreach (var input in audioInputs) args.AddRange(["-i", input.Path]);

        if (audioInputs.Count == 0)
        {
            args.AddRange(["-map", "0:v:0", "-c", "copy"]);
        }
        else if (audioInputs.Count == 1)
        {
            args.AddRange(["-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
        }
        else
        {
            var filter = new StringBuilder();
            var labels = new List<string>();
            for (var i = 0; i < audioInputs.Count; i++)
            {
                var label = $"a{i}";
                filter.Append(CultureInfo.InvariantCulture, $"[{i + 1}:a]volume={audioInputs[i].Volume:0.###}[{label}];");
                labels.Add($"[{label}]");
            }
            filter.Append(string.Join(string.Empty, labels));
            filter.Append(CultureInfo.InvariantCulture, $"amix=inputs={audioInputs.Count}:duration=longest:normalize=0[aout]");
            args.AddRange(["-filter_complex", filter.ToString(), "-map", "0:v:0", "-map", "[aout]",
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2"]);
        }

        args.AddRange(["-movflags", "+faststart"]);
        if (!string.IsNullOrWhiteSpace(title))
        {
            args.AddRange(["-metadata", $"title={title}"]);
        }
        args.Add(output);
        return args;
    }

    public static List<string> Thumbnail(string input, string output, double atSeconds)
    {
        var args = Global();
        args.AddRange([
            "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", input,
            "-frames:v", "1",
            "-vf", "scale=480:-2",
            "-q:v", "4",
            output,
        ]);
        return args;
    }

    public static string ConcatListContent(IEnumerable<string> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files)
        {
            var normalized = Path.GetFullPath(file).Replace('\\', '/');
            normalized = normalized.Replace("'", "'\\''");
            builder.Append("file '").Append(normalized).Append('\'').Append('\n');
        }
        return builder.ToString();
    }

    private static List<string> Global() =>
    [
        "-hide_banner", "-y", "-loglevel", "warning", "-nostats", "-progress", "pipe:1",
    ];

    private static IEnumerable<string> Input(CaptureProfile profile, DisplayInfo display)
    {
        if (profile.Source == CaptureSource.Ddagrab)
        {
            yield return "-f";
            yield return "lavfi";
            yield return "-i";
            yield return $"ddagrab=framerate={profile.Fps}:output_idx={display.Index}:draw_mouse=1";
            yield break;
        }

        yield return "-thread_queue_size";
        yield return "512";
        yield return "-f";
        yield return "gdigrab";
        yield return "-framerate";
        yield return profile.Fps.ToString(CultureInfo.InvariantCulture);
        yield return "-draw_mouse";
        yield return "1";
        yield return "-offset_x";
        yield return display.Bounds.X.ToString(CultureInfo.InvariantCulture);
        yield return "-offset_y";
        yield return display.Bounds.Y.ToString(CultureInfo.InvariantCulture);
        yield return "-video_size";
        yield return $"{display.Bounds.Width}x{display.Bounds.Height}";
        yield return "-i";
        yield return "desktop";
    }

    private static IEnumerable<string> Encoder(CaptureProfile profile, AppSettings settings)
    {
        var quality = QualityPresets.Get(settings.QualityPreset);
        var bitrate = quality.BitrateKbps;
        var maxrate = bitrate * 13 / 10;
        var bufsize = bitrate * 2;
        var fps = profile.Fps.ToString(CultureInfo.InvariantCulture);
        var gop = profile.Fps.ToString(CultureInfo.InvariantCulture);

        switch (profile.Encoder)
        {
            case "h264_nvenc":
                return ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr",
                    "-b:v", $"{bitrate}k", "-maxrate", $"{maxrate}k", "-bufsize", $"{bufsize}k",
                    "-g", gop, "-keyint_min", gop, "-bf", "0"];
            case "h264_amf":
                return ["-c:v", "h264_amf", "-quality", "balanced", "-rc", "vbr_peak",
                    "-b:v", $"{bitrate}k", "-maxrate", $"{maxrate}k", "-g", gop];
            case "h264_qsv":
                return ["-c:v", "h264_qsv", "-preset", "medium", "-b:v", $"{bitrate}k",
                    "-maxrate", $"{maxrate}k", "-g", gop];
            case "h264_mf":
                return ["-c:v", "h264_mf", "-b:v", $"{bitrate}k", "-g", gop];
            default:
                return ["-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
                    "-crf", quality.Crf.ToString(CultureInfo.InvariantCulture),
                    "-maxrate", $"{maxrate}k", "-bufsize", $"{bufsize}k",
                    "-g", gop, "-keyint_min", gop, "-sc_threshold", "0", "-pix_fmt", "yuv420p"];
        }
    }
}
