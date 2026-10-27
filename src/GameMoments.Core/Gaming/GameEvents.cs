namespace GameMoments.Core.Gaming;

/// <summary>事件类型标识（持久化到设置与片段索引，保持稳定英文 id）。</summary>
public static class GameEventKinds
{
    public const string Kill = "kill";
    public const string Headshot = "headshot";
    public const string Multikill = "multikill";
    public const string Assist = "assist";
    public const string Death = "death";
    public const string RoundWin = "round_win";
    public const string RoundLoss = "round_loss";
    public const string BombPlanted = "bomb_planted";
    public const string BombDefused = "bomb_defused";
    public const string BombExploded = "bomb_exploded";
    public const string MatchEnd = "match_end";

    /// <summary>数据通道刚连通，仅用于诊断，不参与自动保存。</summary>
    public const string Connected = "connected";

    /// <summary>用户可以在设置里逐个开关的事件类型。</summary>
    public static IReadOnlyList<string> UserConfigurable { get; } =
    [
        Kill, Headshot, Multikill, Assist, Death,
        RoundWin, RoundLoss,
        BombPlanted, BombDefused, BombExploded,
        MatchEnd,
    ];

    /// <summary>默认开启的事件：只挑真正值得剪的类型，避免误触发刷屏。</summary>
    public static IReadOnlyList<string> DefaultEnabled { get; } =
    [
        Kill, Headshot, Multikill, RoundWin, BombPlanted, BombDefused, BombExploded, MatchEnd,
    ];

    public static string Label(string kind) => kind switch
    {
        Kill => "击杀",
        Headshot => "爆头击杀",
        Multikill => "多杀",
        Assist => "助攻",
        Death => "阵亡",
        RoundWin => "回合胜利",
        RoundLoss => "回合失利",
        BombPlanted => "安放炸弹",
        BombDefused => "拆除炸弹",
        BombExploded => "炸弹爆炸",
        MatchEnd => "比赛结束",
        Connected => "事件源已连接",
        _ => kind,
    };

    /// <summary>合并分组：同一分组内的事件在合并窗口内只剪一段。</summary>
    public static string MergeGroup(string kind) => kind switch
    {
        Kill or Headshot or Multikill => "kill-chain",
        RoundWin or RoundLoss or BombPlanted or BombDefused or BombExploded => "objective",
        MatchEnd => "match",
        Connected => "diagnostic",
        _ => kind,
    };

    public static int Priority(string kind) => kind switch
    {
        MatchEnd => 100,
        BombExploded => 90,
        Multikill => 85,
        BombDefused => 80,
        RoundWin => 70,
        BombPlanted => 70,
        Headshot => 60,
        Kill => 50,
        RoundLoss => 40,
        Assist => 35,
        Death => 30,
        Connected => 0,
        _ => 20,
    };
}

/// <summary>一条来自真实事件源的游戏事件。</summary>
public sealed record GameEvent(
    string GameId,
    string GameName,
    string Kind,
    string Title,
    int Priority,
    double Confidence,
    DateTime OccurredUtc,
    string Source,
    string? Detail = null)
{
    public string KindLabel => GameEventKinds.Label(Kind);
}

public enum EventOutcome
{
    /// <summary>已触发自动保存（结果见片段或错误信息）。</summary>
    Saved,

    /// <summary>只加书签，等用户自己决定是否从缓存保存。</summary>
    Bookmarked,

    /// <summary>落在合并窗口内，已并入上一条事件。</summary>
    Merged,

    /// <summary>该事件类型被关闭。</summary>
    SkippedKindDisabled,

    /// <summary>置信度低于阈值。</summary>
    SkippedLowConfidence,

    /// <summary>处于冷却时间。</summary>
    SkippedCooldown,

    /// <summary>其他原因跳过（例如捕获未在运行）。</summary>
    Skipped,
}

/// <summary>事件规则引擎的判定结果。</summary>
public sealed record EventDecision(
    GameEvent Event,
    EventOutcome Outcome,
    string Message,
    int PreSeconds,
    int PostSeconds,
    string ClipTitle)
{
    public bool WantsClip => Outcome is EventOutcome.Saved or EventOutcome.Bookmarked;
}

/// <summary>界面用的事件记录（可变，保存结果会回填）。</summary>
public sealed class SmartEventRecord
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];

    public required GameEvent Event { get; init; }

    public DateTime RecordedUtc { get; init; } = DateTime.UtcNow;

    public EventOutcome Outcome { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ClipPath { get; set; }
    public string? ClipName { get; set; }
    public bool FalsePositive { get; set; }
    public string? Feedback { get; set; }
    public int PreSeconds { get; init; }
    public int PostSeconds { get; init; }

    /// <summary>事件后继续录制的结束时间（超出后无法再从缓存补存）。</summary>
    public DateTime? WindowEndUtc { get; init; }

    /// <summary>游戏显示名（可能来自目录或自定义项）。</summary>
    public string GameLabel { get; init; } = string.Empty;

    public DateTime OccurredLocal => Event.OccurredUtc.ToLocalTime();
    public string TimeText => OccurredLocal.ToString("HH:mm:ss");
    public string GameName => string.IsNullOrWhiteSpace(GameLabel) ? Event.GameName : GameLabel;
    public string KindLabel => Event.KindLabel;
    public string Title => Event.Title;
    public string Source => Event.Source;
    public string Detail => Event.Detail ?? string.Empty;
    public string ConfidenceText => Event.Confidence >= 0.999 ? "100%" : $"{Event.Confidence * 100:0}%";
    public string WindowText => $"{PreSeconds}s + {PostSeconds}s";

    public string OutcomeText => Outcome switch
    {
        EventOutcome.Saved when !string.IsNullOrWhiteSpace(ClipName) => "已保存 " + ClipName,
        EventOutcome.Saved => "已触发保存",
        EventOutcome.Bookmarked => "书签（可手动保存）",
        EventOutcome.Merged => "已合并",
        EventOutcome.SkippedKindDisabled => "类型已关闭",
        EventOutcome.SkippedLowConfidence => "置信度不足",
        EventOutcome.SkippedCooldown => "冷却中",
        _ => "已跳过",
    };

    public bool CanSaveFromCache => Outcome is EventOutcome.Bookmarked or EventOutcome.Merged;

    public bool HasClip => !string.IsNullOrWhiteSpace(ClipPath) && File.Exists(ClipPath);

    public bool IsSaved => Outcome == EventOutcome.Saved && HasClip;

    public string RowText => $"{TimeText}  {KindLabel}  {Title}  ·  {OutcomeText}";

    public string FeedbackText => FalsePositive
        ? (string.IsNullOrWhiteSpace(Feedback) ? "已标记为误触发" : Feedback!)
        : string.Empty;
}
