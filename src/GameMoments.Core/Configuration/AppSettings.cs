using System.Text.Json.Serialization;
using GameMoments.Core.Hotkeys;

namespace GameMoments.Core.Configuration;

/// <summary>
/// 持久化设置。所有取值在 <see cref="Normalize"/> 中校验并修复，避免损坏的配置文件让应用无法启动。
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>片段保存目录；为空时使用默认 Videos\GameMoments。</summary>
    public string SaveFolder { get; set; } = string.Empty;

    public string FileNamePrefix { get; set; } = "Moments";

    /// <summary>回放缓存时长（秒），允许 15/30/60/120。</summary>
    public int ReplaySeconds { get; set; } = 30;

    /// <summary>触发保存后额外录制的时长（秒），允许 0/5/10。</summary>
    public int PostTriggerSeconds { get; set; }

    /// <summary>目标帧率，允许 30/60。</summary>
    public int Fps { get; set; } = 60;

    /// <summary>质量预设：low / medium / high。</summary>
    public string QualityPreset { get; set; } = "high";

    /// <summary>捕获方式偏好：auto / ddagrab / gdigrab。</summary>
    public string CaptureSourcePreference { get; set; } = "auto";

    /// <summary>编码器偏好：auto / hardware / software。</summary>
    public string EncoderPreference { get; set; } = "auto";

    /// <summary>要捕获的显示器序号（<see cref="Platform.DisplayInfo.Index"/>）。</summary>
    public int DisplayIndex { get; set; }

    public bool CaptureSystemAudio { get; set; } = true;
    public bool CaptureMicrophone { get; set; }

    /// <summary>系统声音回放设备 ID；null 表示默认设备。</summary>
    public string? SystemAudioDeviceId { get; set; }

    /// <summary>麦克风设备 ID；null 表示默认设备。</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>系统声音音量（百分比，0-150）。</summary>
    public int SystemAudioVolume { get; set; } = 100;

    /// <summary>麦克风音量（百分比，0-150）。</summary>
    public int MicrophoneVolume { get; set; } = 80;

    /// <summary>保存回放快捷键，例如 Ctrl+Alt+F10。</summary>
    public string SaveHotkey { get; set; } = "Ctrl+Alt+F10";

    /// <summary>开始/结束完整录制的快捷键。</summary>
    public string ToggleRecordingHotkey { get; set; } = "Ctrl+Alt+F9";

    /// <summary>启动应用后自动开始捕获（进入回放缓存状态）。</summary>
    public bool AutoStartCapture { get; set; }

    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool ShowTrayNotifications { get; set; } = true;

    /// <summary>保存成功后是否在界面/托盘提示。</summary>
    public bool ShowSaveNotifications { get; set; } = true;

    /// <summary>片段保存成功后自动打开片段库页面。</summary>
    public bool OpenLibraryAfterSave { get; set; }

    public bool FirstRunCompleted { get; set; }

    /// <summary>用户手动指定的 ffmpeg.exe（或所在目录），为空则自动查找。</summary>
    public string? FfmpegPath { get; set; }

    public int LogRetentionDays { get; set; } = 7;

    /// <summary>同时允许保存的最大片段数（保存队列上限）。</summary>
    public int MaxQueuedSaves { get; set; } = 4;

    /// <summary>缓存分段占用的磁盘上限（GB）。</summary>
    public int MaxCacheSizeGb { get; set; } = 20;

    // ---- 智能录制 ----

    /// <summary>智能录制总开关：自动检测游戏并按其配置启动捕获/事件识别。</summary>
    public bool SmartRecordingEnabled { get; set; }

    /// <summary>本机游戏状态推送端口（GSI 等事件源），实际端口被占用时会自动顺延。</summary>
    public int SmartGsiPort { get; set; } = 48123;

    /// <summary>按游戏的智能录制配置，键为游戏 id（自定义项以 <see cref="Gaming.GameCatalog.CustomGamePrefix"/> 开头）。</summary>
    public Dictionary<string, Gaming.GameSettings> GameConfigs { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>用户自定义的游戏可执行文件名列表，支持 "game.exe" 或 "game.exe=显示名"。</summary>
    public List<string> CustomGameExecutables { get; set; } = new();

    /// <summary>事件书签快捷键（识别到事件但只加书签，不立即剪辑）。</summary>
    public string BookmarkHotkey { get; set; } = "Ctrl+Alt+F8";

    /// <summary>自动为支持的游戏安装事件源配置（例如 CS2 的 GSI cfg）。</summary>
    public bool InstallGsiConfig { get; set; } = true;

    /// <summary>整局自动录制的单文件时长上限（分钟），0 表示不限制。</summary>
    public int WholeRecordingMinutes { get; set; }

    /// <summary>检测到游戏退出后，延迟多少秒结束整局录制。</summary>
    public int WholeRecordingStopDelaySeconds { get; set; } = 5;

    /// <summary>最近事件列表保留条数。</summary>
    public int MaxEventHistory { get; set; } = 50;

    [JsonIgnore]
    public int FpsOrDefault => Fps == 30 ? 30 : 60;

    /// <summary>读取指定游戏的配置，缺失时按默认值创建并存入。</summary>
    public Gaming.GameSettings GetGameConfig(string gameId)
    {
        GameConfigs ??= new Dictionary<string, Gaming.GameSettings>(StringComparer.OrdinalIgnoreCase);
        if (GameConfigs.TryGetValue(gameId, out var existing) && existing is not null) return existing;
        var created = new Gaming.GameSettings();
        created.EnsureEventKinds(Gaming.GameEventKinds.UserConfigurable);
        GameConfigs[gameId] = created;
        return created;
    }

    /// <summary>修正非法值并返回面向用户的警告列表。</summary>
    public List<string> Normalize(string fallbackSaveFolder)
    {
        var warnings = new List<string>();

        ReplaySeconds = Nearest(ReplaySeconds, [15, 30, 60, 120]);
        PostTriggerSeconds = Nearest(PostTriggerSeconds, [0, 5, 10]);
        Fps = Nearest(Fps, [30, 60]);
        QualityPreset = QualityPresets.Get(QualityPreset).Id;
        CaptureSourcePreference = NormalizeChoice(CaptureSourcePreference, ["auto", "ddagrab", "gdigrab"], "auto",
            warnings, "捕获方式偏好");
        EncoderPreference = NormalizeChoice(EncoderPreference, ["auto", "hardware", "software"], "auto",
            warnings, "编码器偏好");
        DisplayIndex = Math.Max(0, DisplayIndex);
        SystemAudioVolume = Math.Clamp(SystemAudioVolume, 0, 150);
        MicrophoneVolume = Math.Clamp(MicrophoneVolume, 0, 150);
        MaxQueuedSaves = Math.Clamp(MaxQueuedSaves, 1, 10);
        MaxCacheSizeGb = Math.Clamp(MaxCacheSizeGb, 5, 200);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 90);
        SmartGsiPort = Math.Clamp(SmartGsiPort, 1024, 65535);
        WholeRecordingMinutes = Math.Clamp(WholeRecordingMinutes, 0, 600);
        WholeRecordingStopDelaySeconds = Math.Clamp(WholeRecordingStopDelaySeconds, 0, 120);
        MaxEventHistory = Math.Clamp(MaxEventHistory, 10, 500);

        NormalizeGameConfigs(warnings);
        NormalizeCustomGames(warnings);

        var folder = Platform.AppPaths.Normalize(SaveFolder);
        if (folder is null)
        {
            folder = Platform.AppPaths.Normalize(fallbackSaveFolder);
        }
        SaveFolder = folder ?? string.Empty;

        if (string.IsNullOrWhiteSpace(FileNamePrefix))
        {
            FileNamePrefix = "Moments";
        }
        else
        {
            var cleaned = SanitizeFileName(FileNamePrefix.Trim());
            if (cleaned.Length == 0)
            {
                warnings.Add("文件名前缀包含非法字符，已恢复为 Moments。");
                cleaned = "Moments";
            }
            if (cleaned.Length > 40) cleaned = cleaned[..40];
            FileNamePrefix = cleaned;
        }

        if (!HotkeyGesture.TryParse(SaveHotkey, out _, out var saveError))
        {
            warnings.Add($"保存快捷键无效（{saveError}），已重置为 Ctrl+Alt+F10。");
            SaveHotkey = "Ctrl+Alt+F10";
        }
        if (!HotkeyGesture.TryParse(ToggleRecordingHotkey, out _, out var toggleError))
        {
            warnings.Add($"录制快捷键无效（{toggleError}），已重置为 Ctrl+Alt+F9。");
            ToggleRecordingHotkey = "Ctrl+Alt+F9";
        }
        if (string.Equals(SaveHotkey, ToggleRecordingHotkey, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("保存回放与开始/停止录制使用了相同快捷键，注册时只会有一个生效，请修改。");
        }

        if (!HotkeyGesture.TryParse(BookmarkHotkey, out _, out var bookmarkError))
        {
            warnings.Add($"书签快捷键无效（{bookmarkError}），已重置为 Ctrl+Alt+F8。");
            BookmarkHotkey = "Ctrl+Alt+F8";
        }
        if (new[] { SaveHotkey, ToggleRecordingHotkey, BookmarkHotkey }
            .Select(h => h.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() < 3)
        {
            warnings.Add("三个快捷键存在重复，注册时只有第一个生效，请修改。");
        }

        if (!string.IsNullOrWhiteSpace(SystemAudioDeviceId) && SystemAudioDeviceId.Trim().Length == 0)
        {
            SystemAudioDeviceId = null;
        }
        if (!string.IsNullOrWhiteSpace(MicrophoneDeviceId) && MicrophoneDeviceId.Trim().Length == 0)
        {
            MicrophoneDeviceId = null;
        }

        if (!string.IsNullOrWhiteSpace(FfmpegPath))
        {
            FfmpegPath = FfmpegPath.Trim().Trim('"');
            if (FfmpegPath.Length == 0) FfmpegPath = null;
        }

        return warnings;
    }

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.GameConfigs = new Dictionary<string, Gaming.GameSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in GameConfigs ?? new Dictionary<string, Gaming.GameSettings>())
        {
            if (pair.Value is not null) clone.GameConfigs[pair.Key] = pair.Value.Clone();
        }

        clone.CustomGameExecutables = new List<string>(CustomGameExecutables ?? new List<string>());
        return clone;
    }

    private void NormalizeGameConfigs(List<string> warnings)
    {
        GameConfigs ??= new Dictionary<string, Gaming.GameSettings>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new Dictionary<string, Gaming.GameSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in GameConfigs)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null) continue;
            var id = pair.Key.Trim();
            var config = pair.Value;
            var configWarnings = config.Normalize(id);
            warnings.AddRange(configWarnings);
            cleaned[id] = config;
        }

        GameConfigs = cleaned;

        // 内置游戏补齐默认配置，便于界面直接展示。
        foreach (var definition in Gaming.GameCatalog.BuiltIn)
        {
            if (GameConfigs.ContainsKey(definition.Id)) continue;
            var config = new Gaming.GameSettings();
            config.EnsureEventKinds(Gaming.GameEventKinds.UserConfigurable);
            GameConfigs[definition.Id] = config;
        }
    }

    private void NormalizeCustomGames(List<string> warnings)
    {
        CustomGameExecutables ??= new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();
        foreach (var entry in CustomGameExecutables)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var parsed = Gaming.GameCatalog.ParseCustomEntry(entry);
            if (parsed is null)
            {
                warnings.Add($"自定义游戏项“{entry}”无法识别，已忽略。");
                continue;
            }
            var (executable, displayName) = parsed.Value;
            if (!seen.Add(executable)) continue;
            var canonical = executable + ".exe";
            cleaned.Add(displayName is null ? canonical : $"{canonical}={displayName}");
            GetGameConfig(Gaming.GameCatalog.CustomDefinition(executable, displayName).Id);
        }

        if (cleaned.Count > 50)
        {
            warnings.Add("自定义游戏项超过 50 个，多余的已忽略。");
            cleaned = cleaned.Take(50).ToList();
        }

        CustomGameExecutables = cleaned;
    }

    private static int Nearest(int value, int[] allowed)
    {
        var best = allowed[0];
        var bestDistance = int.MaxValue;
        foreach (var candidate in allowed)
        {
            var distance = Math.Abs(candidate - value);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static string NormalizeChoice(string? value, string[] allowed, string fallback, List<string> warnings, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var match = allowed.FirstOrDefault(a => string.Equals(a, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        warnings.Add($"{label}“{value}”无法识别，已恢复为 {fallback}。");
        return fallback;
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        var cleaned = builder.ToString().Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "片段" : cleaned;
    }
}

