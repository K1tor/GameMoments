namespace GameMoments.Core.Configuration;

public sealed record QualityPreset(string Id, string Name, int BitrateKbps, int Crf, string Description);

public static class QualityPresets
{
    public static readonly IReadOnlyList<QualityPreset> All =
    [
        new("low", "流畅", 8_000, 26, "8 Mbps，体积小，适合长时间回放缓存"),
        new("medium", "标准", 14_000, 23, "14 Mbps，画质与体积均衡"),
        new("high", "高清", 24_000, 19, "24 Mbps，高画质，推荐 1080p60"),
    ];

    public static QualityPreset Get(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[1];

    public static int NearestBitrateKbps(string? id) => Get(id).BitrateKbps;

    public static int CrfFor(string? id) => Get(id).Crf;
}
