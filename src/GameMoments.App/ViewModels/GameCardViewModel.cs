using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using GameMoments.Core.Gaming;

namespace GameMoments.App.ViewModels;

/// <summary>下拉选项（值 + 显示文本）。</summary>
public sealed record ChoiceOption(object Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>单个事件类型的开关。</summary>
public sealed class EventKindItem : ObservableObject
{
    private readonly GameSettings _config;
    private readonly Func<Task> _save;
    private bool _enabled;

    public EventKindItem(string kind, GameSettings config, Func<Task> save)
    {
        Kind = kind;
        _config = config;
        _save = save;
        _enabled = config.IsEventEnabled(kind);
    }

    public string Kind { get; }
    public string Label => GameEventKinds.Label(Kind);

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            _config.EventKinds[Kind] = value;
            _ = _save();
        }
    }

    /// <summary>从配置同步状态，不触发保存。</summary>
    public void SetEnabledSilently(bool value)
    {
        if (_enabled == value) return;
        _enabled = value;
        OnPropertyChanged(nameof(Enabled));
    }
}

/// <summary>智能录制页里的一张游戏卡片：支持状态 + 该游戏的事件规则。</summary>
public sealed class GameCardViewModel : ObservableObject
{
    private readonly GameSettings _config;
    private readonly Func<Task> _save;

    private static readonly IReadOnlyList<ChoiceOption> ReplayChoices = new[]
    {
        new ChoiceOption(0, "跟随全局"),
        new ChoiceOption(15, "15 秒"),
        new ChoiceOption(30, "30 秒"),
        new ChoiceOption(60, "60 秒"),
        new ChoiceOption(120, "120 秒"),
        new ChoiceOption(180, "180 秒"),
    };

    private static readonly IReadOnlyList<ChoiceOption> PreChoices = new[]
    {
        new ChoiceOption(5, "5 秒"),
        new ChoiceOption(10, "10 秒"),
        new ChoiceOption(15, "15 秒"),
        new ChoiceOption(20, "20 秒"),
        new ChoiceOption(30, "30 秒"),
    };

    private static readonly IReadOnlyList<ChoiceOption> PostChoices = new[]
    {
        new ChoiceOption(0, "0 秒"),
        new ChoiceOption(3, "3 秒"),
        new ChoiceOption(5, "5 秒"),
        new ChoiceOption(10, "10 秒"),
    };

    private static readonly IReadOnlyList<ChoiceOption> CooldownChoices = new[]
    {
        new ChoiceOption(0, "不冷却"),
        new ChoiceOption(5, "5 秒"),
        new ChoiceOption(8, "8 秒"),
        new ChoiceOption(15, "15 秒"),
        new ChoiceOption(30, "30 秒"),
    };

    private static readonly IReadOnlyList<ChoiceOption> MergeChoices = new[]
    {
        new ChoiceOption(0, "不合并"),
        new ChoiceOption(5, "5 秒"),
        new ChoiceOption(12, "12 秒"),
        new ChoiceOption(20, "20 秒"),
    };

    private static readonly IReadOnlyList<ChoiceOption> ConfidenceChoices = new[]
    {
        new ChoiceOption(0.5, "50% 以上"),
        new ChoiceOption(0.7, "70% 以上"),
        new ChoiceOption(0.8, "80% 以上"),
        new ChoiceOption(0.9, "90% 以上"),
        new ChoiceOption(1.0, "100%（只信官方数据）"),
    };

    public GameCardViewModel(GameDefinition definition, GameSettings config, Func<Task> save)
    {
        Definition = definition;
        _config = config;
        _save = save;
        EventKinds = new ObservableCollection<EventKindItem>(
            GameEventKinds.UserConfigurable.Select(kind => new EventKindItem(kind, config, save)));
    }

    public GameDefinition Definition { get; }
    public string DisplayName => Definition.DisplayName;
    public string SupportText => Definition.SupportsAutomaticEvents
        ? $"自动事件识别：{Definition.EventSourceLabel}"
        : Definition.CaptureOnlyText;
    public bool SupportsAutomaticEvents => Definition.SupportsAutomaticEvents;
    public string Notes => Definition.Notes;
    public bool IsCustom => GameCatalog.IsCustom(Definition.Id);

    public bool Enabled
    {
        get => _config.Enabled;
        set
        {
            if (_config.Enabled == value) return;
            _config.Enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            _ = _save();
        }
    }

    public string StatusText => Enabled
        ? (SupportsAutomaticEvents ? "已启用 · 支持自动事件" : "已启用 · 仅自动检测与录制")
        : "已禁用";

    public IReadOnlyList<ChoiceOption> Modes => GameModes.All
        .Select(mode => new ChoiceOption(mode, GameModes.Label(mode)))
        .ToList();

    public string Mode
    {
        get => _config.Mode;
        set
        {
            if (string.Equals(_config.Mode, value, StringComparison.Ordinal)) return;
            _config.Mode = GameModes.Normalize(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeText));
            _ = _save();
        }
    }

    public string ModeText => GameModes.Label(_config.Mode);

    public IReadOnlyList<ChoiceOption> ReplayOptions => ReplayChoices;
    public IReadOnlyList<ChoiceOption> PreOptions => PreChoices;
    public IReadOnlyList<ChoiceOption> PostOptions => PostChoices;
    public IReadOnlyList<ChoiceOption> CooldownOptions => CooldownChoices;
    public IReadOnlyList<ChoiceOption> MergeOptions => MergeChoices;
    public IReadOnlyList<ChoiceOption> ConfidenceOptions => ConfidenceChoices;

    public object ReplaySeconds
    {
        get => ReplayChoices.FirstOrDefault(o => Equals(o.Value, _config.ReplaySeconds))?.Value ?? 0;
        set
        {
            var seconds = ToInt(value, 0);
            if (_config.ReplaySeconds == seconds) return;
            _config.ReplaySeconds = seconds;
            OnPropertyChanged();
            _ = _save();
        }
    }

    public object PreSeconds
    {
        get => PreChoices.FirstOrDefault(o => Equals(o.Value, _config.PreSeconds))?.Value ?? 10;
        set
        {
            var seconds = ToInt(value, 10);
            if (_config.PreSeconds == seconds) return;
            _config.PreSeconds = seconds;
            OnPropertyChanged();
            _ = _save();
        }
    }

    public object PostSeconds
    {
        get => PostChoices.FirstOrDefault(o => Equals(o.Value, _config.PostSeconds))?.Value ?? 5;
        set
        {
            var seconds = ToInt(value, 5);
            if (_config.PostSeconds == seconds) return;
            _config.PostSeconds = seconds;
            OnPropertyChanged();
            _ = _save();
        }
    }

    public object CooldownSeconds
    {
        get => CooldownChoices.FirstOrDefault(o => Equals(o.Value, _config.CooldownSeconds))?.Value ?? 8;
        set
        {
            var seconds = ToInt(value, 8);
            if (_config.CooldownSeconds == seconds) return;
            _config.CooldownSeconds = seconds;
            OnPropertyChanged();
            _ = _save();
        }
    }

    public object MergeWindowSeconds
    {
        get => MergeChoices.FirstOrDefault(o => Equals(o.Value, _config.MergeWindowSeconds))?.Value ?? 12;
        set
        {
            var seconds = ToInt(value, 12);
            if (_config.MergeWindowSeconds == seconds) return;
            _config.MergeWindowSeconds = seconds;
            OnPropertyChanged();
            _ = _save();
        }
    }

    public object MinConfidence
    {
        get => ConfidenceChoices.FirstOrDefault(o => Equals(o.Value, _config.MinConfidence))?.Value ?? 0.8;
        set
        {
            var value2 = ToDouble(value, 0.8);
            if (Math.Abs(_config.MinConfidence - value2) < 0.0001) return;
            _config.MinConfidence = value2;
            OnPropertyChanged();
            _ = _save();
        }
    }

    public bool AutoSaveEvents
    {
        get => _config.AutoSaveEvents;
        set
        {
            if (_config.AutoSaveEvents == value) return;
            _config.AutoSaveEvents = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SaveModeText));
            _ = _save();
        }
    }

    public string SaveModeText => AutoSaveEvents ? "自动保存片段" : "只加书签（手动补存）";

    public ObservableCollection<EventKindItem> EventKinds { get; }

    public void RefreshFromConfig()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(ReplaySeconds));
        OnPropertyChanged(nameof(PreSeconds));
        OnPropertyChanged(nameof(PostSeconds));
        OnPropertyChanged(nameof(CooldownSeconds));
        OnPropertyChanged(nameof(MergeWindowSeconds));
        OnPropertyChanged(nameof(MinConfidence));
        OnPropertyChanged(nameof(AutoSaveEvents));
        OnPropertyChanged(nameof(SaveModeText));
        OnPropertyChanged(nameof(StatusText));
        foreach (var item in EventKinds)
        {
            item.SetEnabledSilently(_config.IsEventEnabled(item.Kind));
        }
    }

    private static int ToInt(object? value, int fallback)
    {
        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }

    private static double ToDouble(object? value, double fallback)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }
}
