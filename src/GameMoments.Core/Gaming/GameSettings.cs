namespace GameMoments.Core.Gaming;

/// <summary>单个游戏的录制模式。</summary>
public static class GameModes
{
    /// <summary>不自动捕获、不自动保存（仍显示检测状态）。</summary>
    public const string Off = "off";

    /// <summary>游戏启动即开始回放缓存，事件只记书签不自动剪。</summary>
    public const string Replay = "replay";

    /// <summary>回放缓存 + 事件自动保存（或只加书签，取决于事件动作）。</summary>
    public const string Events = "events";

    /// <summary>整局自动录制：启动游戏自动开始，退出游戏安全结束并保存。</summary>
    public const string Whole = "whole";

    /// <summary>整局自动录制 + 事件剪辑同时开启。</summary>
    public const string Both = "both";

    public static IReadOnlyList<string> All { get; } = [Off, Replay, Events, Whole, Both];

    public static string Label(string mode) => mode switch
    {
        Off => "关闭（不自动捕获）",
        Replay => "仅回放缓存（事件只记书签）",
        Events => "回放缓存 + 事件剪辑",
        Whole => "整局自动录制",
        Both => "整局录制 + 事件剪辑",
        _ => "关闭（不自动捕获）",
    };

    public static bool IsValid(string? mode) =>
        mode is not null && All.Any(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? mode) =>
        IsValid(mode) ? All.First(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase)) : Off;
}

/// <summary>按游戏的智能录制配置：捕获模式 + 事件规则。</summary>
public sealed class GameSettings
{
    /// <summary>参与智能录制（关闭后不自动捕获、不自动保存）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>见 <see cref="GameModes"/>。</summary>
    public string Mode { get; set; } = GameModes.Events;

    /// <summary>该游戏的回放缓存时长覆盖值；0 表示跟随全局设置。</summary>
    public int ReplaySeconds { get; set; }

    /// <summary>事件发生前保留的秒数。</summary>
    public int PreSeconds { get; set; } = 10;

    /// <summary>事件发生后继续录制的秒数。</summary>
    public int PostSeconds { get; set; } = 5;

    /// <summary>同一分组两次自动保存之间的冷却（秒）。</summary>
    public int CooldownSeconds { get; set; } = 8;

    /// <summary>合并窗口（秒）：窗口内的同类事件只剪一段。</summary>
    public int MergeWindowSeconds { get; set; } = 12;

    /// <summary>最低置信度（0-1），低于该值的事件不自动保存。</summary>
    public double MinConfidence { get; set; } = 0.8;

    /// <summary>事件动作：true=自动保存，false=只加书签。</summary>
    public bool AutoSaveEvents { get; set; } = true;

    /// <summary>事件类型开关；缺失视为开启。</summary>
    public Dictionary<string, bool> EventKinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool WantsCapture => Enabled && GameModes.Normalize(Mode) != GameModes.Off;

    public bool WantsWholeRecording =>
        Enabled && GameModes.Normalize(Mode) is GameModes.Whole or GameModes.Both;

    public bool WantsEventClips =>
        Enabled && GameModes.Normalize(Mode) is GameModes.Events or GameModes.Both;

    public bool IsEventEnabled(string kind) =>
        !EventKinds.TryGetValue(kind, out var enabled) || enabled;

    /// <summary>补齐缺失的事件类型开关（新版本新增事件类型时自动补默认值）。</summary>
    public void EnsureEventKinds(IEnumerable<string> kinds)
    {
        EventKinds ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in kinds)
        {
            if (!EventKinds.ContainsKey(kind))
            {
                EventKinds[kind] = GameEventKinds.DefaultEnabled.Contains(kind);
            }
        }
    }

    public GameSettings Clone() => new()
    {
        Enabled = Enabled,
        Mode = Mode,
        ReplaySeconds = ReplaySeconds,
        PreSeconds = PreSeconds,
        PostSeconds = PostSeconds,
        CooldownSeconds = CooldownSeconds,
        MergeWindowSeconds = MergeWindowSeconds,
        MinConfidence = MinConfidence,
        AutoSaveEvents = AutoSaveEvents,
        EventKinds = new Dictionary<string, bool>(EventKinds, StringComparer.OrdinalIgnoreCase),
    };

    public List<string> Normalize(string gameId)
    {
        var warnings = new List<string>();
        var label = GameCatalog.Find(gameId)?.DisplayName ?? gameId;

        if (!GameModes.IsValid(Mode))
        {
            warnings.Add($"{label} 的录制模式“{Mode}”无法识别，已重置为事件剪辑。");
            Mode = GameModes.Events;
        }
        else
        {
            Mode = GameModes.Normalize(Mode);
        }

        if (ReplaySeconds != 0 && ReplaySeconds < 10)
        {
            warnings.Add($"{label} 的回放缓存时长 {ReplaySeconds} 秒过短，已改为跟随全局设置。");
            ReplaySeconds = 0;
        }
        ReplaySeconds = ReplaySeconds == 0 ? 0 : Math.Clamp(ReplaySeconds, 10, 300);

        PreSeconds = Math.Clamp(PreSeconds, 3, 120);
        PostSeconds = Math.Clamp(PostSeconds, 0, 30);
        CooldownSeconds = Math.Clamp(CooldownSeconds, 0, 120);
        MergeWindowSeconds = Math.Clamp(MergeWindowSeconds, 0, 120);
        if (double.IsNaN(MinConfidence) || MinConfidence < 0 || MinConfidence > 1)
        {
            warnings.Add($"{label} 的最低置信度无效，已重置为 0.8。");
            MinConfidence = 0.8;
        }

        EnsureEventKinds(GameEventKinds.UserConfigurable);
        return warnings;
    }
}
