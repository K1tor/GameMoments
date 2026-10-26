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

    [JsonIgnore]
    public int FpsOrDefault => Fps == 30 ? 30 : 60;

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

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

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

