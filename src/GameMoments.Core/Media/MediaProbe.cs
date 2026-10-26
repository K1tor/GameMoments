using GameMoments.Core.Logging;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Media;

public sealed record MediaInfo(
    double DurationSeconds,
    int Width,
    int Height,
    double Fps,
    string VideoCodec,
    string? AudioCodec,
    long SizeBytes,
    bool HasVideo,
    bool HasAudio,
    int AudioSampleRate,
    int AudioChannels)
{
    public string ResolutionText => $"{Width}×{Height}";

    public string DurationText => TimeSpan.FromSeconds(Math.Max(0, DurationSeconds)).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
}

public static class MediaProbe
{
    public static MediaInfo? Probe(string ffprobePath, string filePath, AppLogger logger, int timeoutMs = 30_000)
    {
        var result = ProcessRunner.Run(ffprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", filePath], timeoutMs);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            logger.Warn("媒体探测", "ffprobe 无法读取文件", $"{filePath}: {result.OutputTail(400)}");
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            double duration = 0;
            long size = 0;

            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var durationElement)
                    && double.TryParse(durationElement.GetString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsedDuration))
                {
                    duration = parsedDuration;
                }
                if (format.TryGetProperty("size", out var sizeElement)
                    && long.TryParse(sizeElement.GetString(), out var parsedSize))
                {
                    size = parsedSize;
                }
            }

            if (size <= 0 && File.Exists(filePath))
            {
                size = new FileInfo(filePath).Length;
            }

            var width = 0;
            var height = 0;
            var fps = 0d;
            var videoCodec = string.Empty;
            var hasVideo = false;
            var hasAudio = false;
            string? audioCodec = null;
            var sampleRate = 0;
            var channels = 0;

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var typeElement) ? typeElement.GetString() : null;
                    if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase) && !hasVideo)
                    {
                        hasVideo = true;
                        videoCodec = stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? string.Empty : string.Empty;
                        width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                        if (stream.TryGetProperty("avg_frame_rate", out var avg) && TryParseRate(avg.GetString(), out var avgFps))
                        {
                            fps = avgFps;
                        }
                        else if (stream.TryGetProperty("r_frame_rate", out var raw) && TryParseRate(raw.GetString(), out var rawFps))
                        {
                            fps = rawFps;
                        }
                        if (duration <= 0 && stream.TryGetProperty("duration", out var streamDuration)
                            && double.TryParse(streamDuration.GetString(), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var streamSeconds))
                        {
                            duration = streamSeconds;
                        }
                    }
                    else if (string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase) && !hasAudio)
                    {
                        hasAudio = true;
                        audioCodec = stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() : null;
                        sampleRate = stream.TryGetProperty("sample_rate", out var sr) && int.TryParse(sr.GetString(), out var parsedRate) ? parsedRate : 0;
                        channels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0;
                    }
                }
            }

            var rounded = Math.Round(duration, 3);
            return new MediaInfo(rounded, width, height, Math.Round(fps, 3), videoCodec, audioCodec, size, hasVideo, hasAudio, sampleRate, channels);
        }
        catch (Exception ex)
        {
            logger.Error("媒体探测", "解析 ffprobe 输出失败", ex, result.OutputTail(500));
            return null;
        }
    }

    private static bool TryParseRate(string? text, out double fps)
    {
        fps = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0)
        {
            fps = numerator / denominator;
            return fps > 0;
        }
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out fps) && fps > 0;
    }
}
