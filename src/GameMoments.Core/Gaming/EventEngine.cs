using GameMoments.Core.Configuration;
using GameMoments.Core.Logging;

namespace GameMoments.Core.Gaming;

/// <summary>
/// 事件规则引擎：决定每个游戏事件是否值得剪辑、是否冷却、是否合并进上一段，
/// 并维护「最近事件」列表（含保存结果与误触发反馈）。
/// </summary>
public sealed class EventEngine
{
    private readonly AppLogger _logger;
    private readonly Func<AppSettings> _settings;
    private readonly Func<bool>? _isCaptureReady;
    private readonly List<SmartEventRecord> _history = new();
    private readonly Dictionary<string, DateTime> _lastSaveByGroup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SmartEventRecord> _lastRecordByGroup = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _suppressed;

    public EventEngine(AppLogger logger, Func<AppSettings> settings, Func<bool>? isCaptureReady = null)
    {
        _logger = logger;
        _settings = settings;
        _isCaptureReady = isCaptureReady;
    }

    /// <summary>需要真正剪辑时触发（由智能录制服务负责保存）。</summary>
    public event Action<EventDecision, SmartEventRecord>? SaveRequested;

    /// <summary>新增一条事件记录（已包含最初的判定结果）。</summary>
    public event Action<SmartEventRecord>? RecordAdded;

    /// <summary>记录被更新（保存完成/失败/误触发反馈）。</summary>
    public event Action<SmartEventRecord>? RecordUpdated;

    /// <summary>被规则抑制的事件计数（用于界面显示“已忽略 N 个”）。</summary>
    public int SuppressedCount => Volatile.Read(ref _suppressed);

    public IReadOnlyList<SmartEventRecord> History
    {
        get
        {
            lock (_gate) return _history.ToArray();
        }
    }

    /// <summary>评估一个事件；返回其判定结果（不会为 null，便于显示“为什么没保存”）。</summary>
    public EventDecision Evaluate(GameEvent gameEvent)
    {
        var settings = _settings();
        var config = settings.GetGameConfig(gameEvent.GameId);
        var gameLabel = ResolveGameLabel(gameEvent);

        EventOutcome outcome;
        string message;
        var save = false;

        if (!settings.SmartRecordingEnabled)
        {
            outcome = EventOutcome.Skipped;
            message = "智能录制总开关已关闭，事件只记录在诊断列表里。";
        }
        else if (!config.Enabled)
        {
            outcome = EventOutcome.Skipped;
            message = $"{gameLabel} 的智能录制已单独关闭。";
        }
        else if (!config.WantsEventClips)
        {
            outcome = EventOutcome.Bookmarked;
            message = "当前模式只加书签（未开启事件剪辑），可随时从回放缓存补存。";
        }
        else if (!config.IsEventEnabled(gameEvent.Kind))
        {
            outcome = EventOutcome.SkippedKindDisabled;
            message = $"事件类型「{GameEventKinds.Label(gameEvent.Kind)}」已关闭。";
        }
        else if (gameEvent.Confidence < config.MinConfidence)
        {
            outcome = EventOutcome.SkippedLowConfidence;
            message = $"置信度 {gameEvent.Confidence:0.00} 低于阈值 {config.MinConfidence:0.00}。";
        }
        else if (_isCaptureReady is not null && !_isCaptureReady())
        {
            outcome = EventOutcome.Bookmarked;
            message = "捕获当前没有运行，先加书签；捕获启动后可从回放缓存补存。";
        }
        else
        {
            var group = GameEventKinds.MergeGroup(gameEvent.Kind);
            var key = gameEvent.GameId + "|" + group;
            var now = DateTime.UtcNow;

            SmartEventRecord? mergedInto = null;
            lock (_gate)
            {
                if (config.MergeWindowSeconds > 0
                    && _lastRecordByGroup.TryGetValue(key, out var previous)
                    && (now - previous.RecordedUtc).TotalSeconds <= config.MergeWindowSeconds)
                {
                    mergedInto = previous;
                }
            }

            if (mergedInto is not null)
            {
                outcome = EventOutcome.Merged;
                message = $"与 {config.MergeWindowSeconds} 秒内的同一事件簇合并，不再单独剪辑。";
            }
            else if (config.CooldownSeconds > 0 && IsCoolingDown(key, config.CooldownSeconds, now))
            {
                outcome = EventOutcome.SkippedCooldown;
                message = $"冷却中（同一类事件 {config.CooldownSeconds} 秒内不重复保存）。";
            }
            else if (!config.AutoSaveEvents)
            {
                outcome = EventOutcome.Bookmarked;
                message = "已加入书签（当前为“只加书签”模式），可手动补存。";
            }
            else
            {
                outcome = EventOutcome.Saved;
                message = $"自动保存：事件前 {config.PreSeconds} 秒 + 事件后 {config.PostSeconds} 秒。";
                save = true;
            }
        }

        var title = BuildClipTitle(gameEvent, gameLabel);
        var decision = new EventDecision(gameEvent, outcome, message, config.PreSeconds, config.PostSeconds, title);

        var record = new SmartEventRecord
        {
            Event = gameEvent,
            Outcome = outcome,
            Message = message,
            PreSeconds = config.PreSeconds,
            PostSeconds = config.PostSeconds,
            WindowEndUtc = gameEvent.OccurredUtc.AddSeconds(config.PostSeconds),
            GameLabel = gameLabel,
        };

        lock (_gate)
        {
            _history.Insert(0, record);
            var limit = Math.Max(10, _settings().MaxEventHistory);
            while (_history.Count > limit) _history.RemoveAt(_history.Count - 1);

            if (outcome is EventOutcome.Saved or EventOutcome.Bookmarked or EventOutcome.Merged)
            {
                var group = GameEventKinds.MergeGroup(gameEvent.Kind);
                _lastRecordByGroup[gameEvent.GameId + "|" + group] = record;
            }
        }

        if (outcome is EventOutcome.Skipped or EventOutcome.SkippedKindDisabled
            or EventOutcome.SkippedLowConfidence or EventOutcome.SkippedCooldown)
        {
            Interlocked.Increment(ref _suppressed);
        }

        _logger.Info("事件", $"事件判定：{gameLabel} / {GameEventKinds.Label(gameEvent.Kind)} → {OutcomeText(outcome)}", message);
        TryRaise(RecordAdded, record);

        if (save)
        {
            TryRaise(() => SaveRequested?.Invoke(decision, record));
        }

        return decision;
    }

    /// <summary>把保存结果写回事件记录。</summary>
    public void AttachClip(SmartEventRecord record, string? clipPath, string? clipName, bool success, string? message = null)
    {
        record.ClipPath = success ? clipPath : null;
        record.ClipName = success ? clipName : null;
        if (!success) record.Outcome = EventOutcome.Skipped;
        if (!string.IsNullOrWhiteSpace(message)) record.Message = message!;
        TryRaise(RecordUpdated, record);
    }

    /// <summary>标记/取消误触发反馈。</summary>
    public void SetFalsePositive(SmartEventRecord record, bool value)
    {
        record.FalsePositive = value;
        if (value)
        {
            record.Feedback = "已标记为误触发：可据此调整该游戏的事件规则。";
            _logger.Info("事件", $"用户标记误触发：{record.GameName} / {record.KindLabel}");
        }
        else
        {
            record.Feedback = null;
        }

        TryRaise(RecordUpdated, record);
    }

    /// <summary>记录冷却起点（保存成功后调用）。</summary>
    public void MarkSaved(string gameId, string kind, DateTime utc)
    {
        var key = gameId + "|" + GameEventKinds.MergeGroup(kind);
        lock (_gate) _lastSaveByGroup[key] = utc;
    }

    /// <summary>诊断用：清空历史。</summary>
    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            _lastRecordByGroup.Clear();
            _lastSaveByGroup.Clear();
        }
    }

    private bool IsCoolingDown(string key, int cooldownSeconds, DateTime now)
    {
        lock (_gate)
        {
            return _lastSaveByGroup.TryGetValue(key, out var last) && (now - last).TotalSeconds < cooldownSeconds;
        }
    }

    private static string ResolveGameLabel(GameEvent gameEvent) =>
        string.IsNullOrWhiteSpace(gameEvent.GameName)
            ? (GameCatalog.Find(gameEvent.GameId)?.DisplayName ?? gameEvent.GameId)
            : gameEvent.GameName!;

    private static string BuildClipTitle(GameEvent gameEvent, string gameLabel)
    {
        var kind = GameEventKinds.Label(gameEvent.Kind);
        var stamp = gameEvent.OccurredUtc.ToLocalTime().ToString("HHmmss");
        return $"{gameLabel} {kind} {stamp}";
    }

    public static string OutcomeText(EventOutcome outcome) => outcome switch
    {
        EventOutcome.Saved => "已保存",
        EventOutcome.Bookmarked => "已加书签",
        EventOutcome.Merged => "已合并",
        EventOutcome.SkippedKindDisabled => "事件关闭",
        EventOutcome.SkippedLowConfidence => "置信度不足",
        EventOutcome.SkippedCooldown => "冷却中",
        _ => "已忽略",
    };

    private void TryRaise(Action<SmartEventRecord>? handler, SmartEventRecord record)
    {
        try
        {
            handler?.Invoke(record);
        }
        catch (Exception ex)
        {
            _logger.Warn("事件", "事件记录订阅者异常", ex.Message);
        }
    }

    private void TryRaise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Warn("事件", "事件处理订阅者异常", ex.Message);
        }
    }
}
