using System.Text.Json.Serialization;

namespace GameMoments.Core.Clips;

/// <summary>片段库中的一条记录。字段保持向后兼容，读取时缺失的值使用默认值。</summary>
public sealed class ClipRecord
{
    public string Id { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;

    /// <summary>片段开始时间（本地时间）。</summary>
    public DateTime CreatedAt { get; init; }

    public DateTime SavedAtUtc { get; init; }
    public double DurationSeconds { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public long FileSize { get; init; }
    public bool HasAudio { get; init; }
    public string AudioSummary { get; init; } = string.Empty;
    public string? ThumbnailPath { get; init; }

    /// <summary>是否来自“开始/停止录制”的完整录制，而不是即时回放。</summary>
    public bool ManualRecording { get; init; }

    [JsonIgnore]
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "—";

    [JsonIgnore]
    public string DurationText
    {
        get
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }
    }

    [JsonIgnore]
    public string SizeText => Platform.AppPaths.FormatBytes(FileSize);

    [JsonIgnore]
    public string CreatedText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string FpsText => Fps > 0 ? $"{Fps:0.##} fps" : "—";

    [JsonIgnore]
    public string SourceText => ManualRecording ? "完整录制" : "即时回放";

    [JsonIgnore]
    public string AudioText => HasAudio ? (string.IsNullOrWhiteSpace(AudioSummary) ? "含音频" : AudioSummary) : "无音频";
}
