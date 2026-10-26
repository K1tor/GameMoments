using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameMoments.Core.Audio;
using GameMoments.Core.Capture;
using GameMoments.Core.Clips;
using GameMoments.Core.Configuration;
using GameMoments.Core.Hotkeys;
using GameMoments.Core.Logging;
using GameMoments.Core.Platform;
using GameMoments.Core.Support;
using GameMoments.App.Services;
using PlatformDisplays = GameMoments.Core.Platform.Displays;

namespace GameMoments.App.ViewModels;

/// <summary>主界面的数据与操作。所有后台事件都回到 UI 线程再更新绑定属性。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private readonly List<ClipRecord> _allClips = new();

    private string _stateText = "未就绪";
    private string _statusMessage = "正在检测录制环境…";
    private string _cacheText = "缓存 0.0 秒";
    private double _cachePercent;
    private string _fpsText = "— fps";
    private string _profileText = "尚未开始";
    private string _audioText = "未启用音频";
    private string _captureButtonText = "开始捕获";
    private string _recordButtonText = "开始完整录制";
    private string _saveButtonText = "保存精彩时刻";
    private string _hotkeyError = string.Empty;
    private string _setupError = string.Empty;
    private string _ffmpegText = "检测中…";
    private string _searchText = string.Empty;
    private string _clipCountText = "还没有片段";
    private string _lastSavedText = string.Empty;
    private string _audioWarning = string.Empty;
    private bool _isCapturing;
    private bool _isRecording;
    private bool _isSaving;
    private bool _isBusy;
    private bool _canSave;
    private bool _hasEngine;
    private bool _showFirstRun;
    private bool _showSetupBanner;
    private SolidColorBrush _statusBrush = new(Color.FromRgb(0x98, 0xA2, 0xB3));

    public MainViewModel(AppServices services, Dispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;

        SystemAudioDevices = new ObservableCollection<AudioDeviceInfo>();
        MicrophoneDevices = new ObservableCollection<AudioDeviceInfo>();
        Displays = new ObservableCollection<DisplayInfo>();
        Clips = new ObservableCollection<ClipRecord>();
        RecentClips = new ObservableCollection<ClipRecord>();
        Logs = new ObservableCollection<LogEntry>();

        ShowFirstRun = !services.Settings.FirstRunCompleted;

        if (services.Engine is not null)
        {
            services.Engine.Changed += OnEngineChanged;
        }
        services.Library.Changed += OnLibraryChanged;
        services.Logger.EntryWritten += OnLogEntry;
        foreach (var entry in services.Logger.Snapshot().Reverse().Take(200))
        {
            Logs.Add(entry);
        }

        RefreshClips();
        RefreshDevices();
        UpdateFromEngine();
    }

    public AppSettings Settings => _services.Settings;
    public AppServices Services => _services;

    public void ReloadLogs()
    {
        Logs.Clear();
        foreach (var entry in _services.Logger.Snapshot().Reverse().Take(200))
        {
            Logs.Add(entry);
        }
    }
    public CaptureEngine? Engine => _services.Engine;

    public ObservableCollection<AudioDeviceInfo> SystemAudioDevices { get; }
    public ObservableCollection<AudioDeviceInfo> MicrophoneDevices { get; }
    public ObservableCollection<DisplayInfo> Displays { get; }
    public ObservableCollection<ClipRecord> Clips { get; }
    public ObservableCollection<ClipRecord> RecentClips { get; }
    public ObservableCollection<LogEntry> Logs { get; }

    public string VersionText => $"v{_services.Version}";
    public string SaveFolderText => _services.Settings.SaveFolder;
    public string HotkeySaveText => _services.Settings.SaveHotkey;
    public string HotkeyRecordText => _services.Settings.ToggleRecordingHotkey;
    public string DataFolderText => _services.Paths.DataDirectory;

    public event Action<ClipRecord>? ClipSaved;
    public event Action<string>? Notice;
    public event Action? HotkeysChanged;

    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string CacheText { get => _cacheText; private set => SetProperty(ref _cacheText, value); }
    public double CachePercent { get => _cachePercent; private set => SetProperty(ref _cachePercent, value); }
    public string FpsText { get => _fpsText; private set => SetProperty(ref _fpsText, value); }
    public string ProfileText { get => _profileText; private set => SetProperty(ref _profileText, value); }
    public string AudioText { get => _audioText; private set => SetProperty(ref _audioText, value); }
    public string AudioWarning { get => _audioWarning; private set => SetProperty(ref _audioWarning, value); }
    public string CaptureButtonText { get => _captureButtonText; private set => SetProperty(ref _captureButtonText, value); }
    public string RecordButtonText { get => _recordButtonText; private set => SetProperty(ref _recordButtonText, value); }
    public string SaveButtonText { get => _saveButtonText; private set => SetProperty(ref _saveButtonText, value); }
    public string HotkeyErrorText { get => _hotkeyError; set => SetProperty(ref _hotkeyError, value); }
    public string SetupErrorText { get => _setupError; private set => SetProperty(ref _setupError, value); }
    public string FfmpegText { get => _ffmpegText; private set => SetProperty(ref _ffmpegText, value); }
    public string ClipCountText { get => _clipCountText; private set => SetProperty(ref _clipCountText, value); }
    public string LastSavedText { get => _lastSavedText; private set => SetProperty(ref _lastSavedText, value); }
    public bool IsCapturing { get => _isCapturing; private set => SetProperty(ref _isCapturing, value); }
    public bool IsRecording { get => _isRecording; private set => SetProperty(ref _isRecording, value); }
    public bool IsSaving { get => _isSaving; private set => SetProperty(ref _isSaving, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public bool CanSave { get => _canSave; private set => SetProperty(ref _canSave, value); }
    public bool HasEngine { get => _hasEngine; private set => SetProperty(ref _hasEngine, value); }
    public bool ShowSetupBanner { get => _showSetupBanner; private set => SetProperty(ref _showSetupBanner, value); }
    public SolidColorBrush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }

    public bool ShowFirstRun
    {
        get => _showFirstRun;
        set => SetProperty(ref _showFirstRun, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        StatusMessage = "正在检测 ffmpeg 与硬件编码能力…";
        try
        {
            await _services.DetectAsync();
            HasEngine = _services.Engine is not null;
            FfmpegText = _services.Tools is null
                ? "未找到 ffmpeg"
                : $"{_services.Tools.Version.Split(' ').FirstOrDefault()} · " +
                  (_services.Capabilities?.HasHardwareEncoder == true ? "硬件编码可用" : "仅软件编码");
            SetupErrorText = _services.SetupError ?? string.Empty;
            ShowSetupBanner = _services.Engine is null;

            if (_services.Engine is not null)
            {
                _services.Engine.Changed += OnEngineChanged;
            }

            RefreshDisplays();
            RefreshDevices();
            UpdateFromEngine();

            if (_services.Engine is not null && _services.Settings.AutoStartCapture)
            {
                await StartCaptureAsync();
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("应用", "初始化失败", ex);
            SetupErrorText = ex.Message;
            ShowSetupBanner = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RefreshDisplays()
    {
        Displays.Clear();
        try
        {
            foreach (var display in PlatformDisplays.Enumerate()) Displays.Add(display);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("显示器", "枚举显示器失败", ex);
        }

        if (Displays.Count == 0)
        {
            StatusMessage = "没有检测到显示器，无法开始捕获。";
        }
    }

    public void RefreshDevices()
    {
        SystemAudioDevices.Clear();
        MicrophoneDevices.Clear();
        SystemAudioDevices.Add(new AudioDeviceInfo(string.Empty, "（系统默认设备）", true, AudioDeviceKind.Render));
        MicrophoneDevices.Add(new AudioDeviceInfo(string.Empty, "（系统默认麦克风）", true, AudioDeviceKind.Capture));
        _services.AudioError = null;

        try
        {
            foreach (var device in AudioDeviceService.Enumerate(AudioDeviceKind.Render))
            {
                SystemAudioDevices.Add(device);
            }
        }
        catch (Exception ex)
        {
            _services.AudioError = ex.Message;
            _services.Logger.Warn("音频", "枚举回放设备失败", ex.Message);
        }

        try
        {
            foreach (var device in AudioDeviceService.Enumerate(AudioDeviceKind.Capture))
            {
                MicrophoneDevices.Add(device);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("音频", "枚举麦克风失败", ex.Message);
        }
    }

    public async Task ToggleCaptureAsync()
    {
        if (_services.Engine is null)
        {
            SetupErrorText = _services.SetupError ?? "录制环境未就绪。";
            ShowSetupBanner = true;
            return;
        }

        if (_services.Engine.IsRunning)
        {
            await StopCaptureAsync();
        }
        else
        {
            await StartCaptureAsync();
        }
    }

    public async Task StartCaptureAsync()
    {
        if (_services.Engine is null)
        {
            ShowSetupBanner = true;
            return;
        }

        if (Displays.Count == 0)
        {
            Notice?.Invoke("没有检测到显示器。");
            return;
        }

        var display = Displays.FirstOrDefault(d => d.Index == Settings.DisplayIndex) ?? Displays[0];
        IsBusy = true;
        StatusMessage = "正在检测捕获与编码组合…";
        try
        {
            var result = await _services.Engine.StartAsync(Settings, display, _services.Library, _services.Assembler!);
            if (!result.Success)
            {
                StatusMessage = "启动失败：" + result.Error;
                SetupErrorText = result.Error ?? string.Empty;
                _services.Logger.Error("捕获", "启动捕获失败", null, result.Error);
                Notice?.Invoke("无法开始捕获：" + result.Error);
            }
            else
            {
                _services.Logger.Info("捕获", "开始捕获", _services.Engine.Profile?.DisplayName);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "启动失败：" + ex.Message;
            _services.Logger.Error("捕获", "启动捕获异常", ex);
        }
        finally
        {
            IsBusy = false;
            UpdateFromEngine();
        }
    }

    public async Task StopCaptureAsync()
    {
        if (_services.Engine is null) return;
        IsBusy = true;
        try
        {
            await _services.Engine.StopAsync();
        }
        catch (Exception ex)
        {
            _services.Logger.Error("捕获", "停止捕获失败", ex);
        }
        finally
        {
            IsBusy = false;
            UpdateFromEngine();
        }
    }

    public async Task SaveReplayAsync()
    {
        if (_services.Engine is null || !_services.Engine.IsRunning)
        {
            Notice?.Invoke("请先开始捕获。");
            return;
        }

        IsSaving = true;
        SaveButtonText = "正在保存…";
        try
        {
            var outcome = await _services.Engine.SaveReplayAsync();
            if (!outcome.Success)
            {
                _services.Logger.Warn("片段", "保存失败", outcome.Error);
                Notice?.Invoke("保存失败：" + outcome.Error);
                StatusMessage = "保存失败：" + outcome.Error;
            }
            else if (outcome.Clip is not null)
            {
                AfterSaved(outcome.Clip);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("片段", "保存片段异常", ex);
            Notice?.Invoke("保存失败：" + ex.Message);
        }
        finally
        {
            IsSaving = false;
            SaveButtonText = "保存精彩时刻";
            UpdateFromEngine();
        }
    }

    public async Task ToggleRecordAsync()
    {
        if (_services.Engine is null) return;

        if (_services.Engine.IsManualRecording)
        {
            IsBusy = true;
            try
            {
                var outcome = await _services.Engine.StopManualRecordingAsync();
                if (outcome.Success && outcome.Clip is not null) AfterSaved(outcome.Clip);
                else if (!outcome.Success) Notice?.Invoke("保存录制失败：" + outcome.Error);
            }
            catch (Exception ex)
            {
                _services.Logger.Error("录制", "停止完整录制失败", ex);
            }
            finally
            {
                IsBusy = false;
                UpdateFromEngine();
            }
            return;
        }

        if (!_services.Engine.IsRunning) return;
        var started = await _services.Engine.StartManualRecordingAsync();
        if (!started.Success)
        {
            Notice?.Invoke("开始录制失败：" + started.Error);
        }
        UpdateFromEngine();
    }

    private void AfterSaved(ClipRecord clip)
    {
        LastSavedText = $"最近保存：{clip.FileName} · {clip.DurationText} · {clip.SizeText}";
        StatusMessage = $"已保存 {clip.FileName}";
        RefreshClips();
        ClipSaved?.Invoke(clip);
    }

    public Task<bool> SaveSettingsAsync()
    {
        try
        {
            _services.SaveSettings();
            RaiseStaticProperties();
            _services.Logger.Info("设置", "设置已保存");

            var restart = _services.Engine is not null && _services.Engine.IsRunning;
            if (restart && _services.Engine!.IsManualRecording)
            {
                Notice?.Invoke("设置已保存；完整录制进行中，新设置将在下次开始时生效。");
            }
            else if (restart)
            {
                _ = RestartCaptureAsync();
            }

            HotkeysChanged?.Invoke();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("设置", "保存设置失败", ex);
            Notice?.Invoke("保存设置失败：" + ex.Message);
            return Task.FromResult(false);
        }
    }

    private async Task RestartCaptureAsync()
    {
        try
        {
            await StopCaptureAsync();
            await StartCaptureAsync();
        }
        catch (Exception ex)
        {
            _services.Logger.Error("捕获", "应用新设置时重启捕获失败", ex);
        }
    }

    public void ResetSettings()
    {
        var defaults = new AppSettings();
        var warnings = defaults.Normalize(_services.Paths.DefaultSaveDirectory);
        _services.Store.Save(defaults);
        _services.Settings.SaveFolder = defaults.SaveFolder;
        _services.Store.Save(_services.Settings);
        RaiseStaticProperties();
        RefreshDevices();
        HotkeysChanged?.Invoke();
        Notice?.Invoke(warnings.Count > 0 ? "已恢复默认设置（部分值已修正）。" : "已恢复默认设置。");
    }

    public void RaiseStaticProperties()
    {
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(SaveFolderText));
        OnPropertyChanged(nameof(HotkeySaveText));
        OnPropertyChanged(nameof(HotkeyRecordText));
        OnPropertyChanged(nameof(AudioText));
        OnPropertyChanged(nameof(DataFolderText));
    }

    public bool SetHotkey(bool saveHotkey, HotkeyGesture gesture, out string? error)
    {
        error = null;
        var other = saveHotkey ? Settings.ToggleRecordingHotkey : Settings.SaveHotkey;
        if (string.Equals(other, gesture.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            error = "两个快捷键不能相同。";
            return false;
        }

        if (saveHotkey) Settings.SaveHotkey = gesture.ToString();
        else Settings.ToggleRecordingHotkey = gesture.ToString();
        _services.SaveSettings();
        RaiseStaticProperties();
        HotkeysChanged?.Invoke();
        return true;
    }

    public void CompleteFirstRun()
    {
        Settings.FirstRunCompleted = true;
        _services.SaveSettings();
        ShowFirstRun = false;
    }

    public void RefreshClips()
    {
        _allClips.Clear();
        _allClips.AddRange(_services.Library.All);
        ApplyFilter();
        UpdateClipStats();
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;
        var filtered = query.Length == 0
            ? _allClips
            : _allClips.Where(c => c.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        Clips.Clear();
        foreach (var clip in filtered) Clips.Add(clip);

        RecentClips.Clear();
        foreach (var clip in _allClips.Take(3)) RecentClips.Add(clip);
    }

    private void UpdateClipStats()
    {
        var total = _allClips.Sum(c => c.FileSize);
        ClipCountText = _allClips.Count == 0
            ? "还没有片段"
            : $"共 {_allClips.Count} 个片段 · 占用 {AppPaths.FormatBytes(total)}";
    }

    public void PlayClip(ClipRecord clip)
    {
        try
        {
            Process.Start(new ProcessStartInfo(clip.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notice?.Invoke("无法播放：" + ex.Message);
        }
    }

    public void RevealClip(ClipRecord clip)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{clip.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notice?.Invoke("无法打开位置：" + ex.Message);
        }
    }

    public bool DeleteClip(ClipRecord clip, out string? error)
    {
        error = null;
        if (!File.Exists(clip.FilePath))
        {
            _services.Library.Remove(clip.Id);
            RefreshClips();
            return true;
        }

        if (!RecycleBin.TryRecycle(clip.FilePath, out error))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(clip.ThumbnailPath) && File.Exists(clip.ThumbnailPath))
        {
            try
            {
                File.Delete(clip.ThumbnailPath);
            }
            catch
            {
                // 缩略图删不掉不影响片段删除。
            }
        }

        _services.Library.Remove(clip.Id);
        RefreshClips();
        _services.Logger.Info("片段", "已删除片段", clip.FileName);
        return true;
    }

    public bool RenameClip(ClipRecord clip, string? newName, out string? error)
    {
        if (!_services.Library.Rename(clip.Id, newName, out error))
        {
            return false;
        }
        RefreshClips();
        return true;
    }

    public void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notice?.Invoke("无法打开目录：" + ex.Message);
        }
    }

    public string ExportDiagnostics(string outputPath)
    {
        var path = DiagnosticsBundle.Create(
            outputPath,
            _services.Paths,
            _services.Version,
            _services.Tools,
            _services.Capabilities,
            _services.Engine?.LastError ?? _services.SetupError);
        _services.Logger.Info("诊断", "已导出诊断包", Privacy.Redact(path));
        return path;
    }

    public async Task ShutdownAsync()
    {
        try
        {
            if (_services.Engine is not null)
            {
                _services.Engine.Changed -= OnEngineChanged;
            }
            _services.Library.Changed -= OnLibraryChanged;
            _services.Logger.EntryWritten -= OnLogEntry;
            await _services.ShutdownAsync();
        }
        catch
        {
            // 退出流程不再抛出。
        }
    }

    private void OnEngineChanged()
    {
        if (_dispatcher.CheckAccess()) UpdateFromEngine();
        else _dispatcher.BeginInvoke(UpdateFromEngine);
    }

    private void OnLibraryChanged()
    {
        if (_dispatcher.CheckAccess()) RefreshClips();
        else _dispatcher.BeginInvoke(RefreshClips);
    }

    private void OnLogEntry(LogEntry entry)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Logs.Insert(0, entry);
            while (Logs.Count > 400) Logs.RemoveAt(Logs.Count - 1);
        });
    }

    private void UpdateFromEngine()
    {
        var engine = _services.Engine;
        if (engine is null)
        {
            StateText = "未就绪";
            StatusBrush = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3));
            StatusMessage = "缺少 ffmpeg，无法开始捕获。";
            CaptureButtonText = "开始捕获";
            RecordButtonText = "开始完整录制";
            CanSave = false;
            return;
        }

        IsCapturing = engine.IsRunning;
        IsRecording = engine.IsManualRecording;
        CanSave = engine.State == CaptureState.Replaying && !IsSaving;
        CaptureButtonText = engine.IsRunning ? "停止捕获" : "开始捕获";
        RecordButtonText = engine.IsManualRecording ? "停止并保存录制" : "开始完整录制";
        StatusMessage = engine.StatusMessage;
        var shownCache = Math.Min(engine.CacheSeconds, Settings.ReplaySeconds);
        CacheText = $"缓存 {shownCache:0.0} 秒 / {Settings.ReplaySeconds} 秒";
        CachePercent = engine.IsRunning
            ? Math.Clamp(shownCache / Math.Max(1, Settings.ReplaySeconds) * 100, 0, 100)
            : 0;
        FpsText = engine.IsRunning ? $"{engine.CapturedFps:0.0} fps" : "— fps";
        ProfileText = engine.Profile?.DisplayName ?? "尚未开始";
        AudioText = engine.IsRunning
            ? engine.AudioSummary
            : BuildAudioSummary();
        AudioWarning = engine.AudioWarning ?? string.Empty;

        switch (engine.State)
        {
            case CaptureState.Starting:
                StateText = "启动中";
                StatusBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA6, 0x3A));
                break;
            case CaptureState.Replaying:
                StateText = "缓存中就绪";
                StatusBrush = new SolidColorBrush(Color.FromRgb(0x35, 0xC0, 0x7A));
                break;
            case CaptureState.Recording:
                StateText = "完整录制中";
                StatusBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
                break;
            case CaptureState.Faulted:
                StateText = "出错";
                StatusBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
                break;
            case CaptureState.Stopping:
            case CaptureState.Idle:
            default:
                StateText = "未启动";
                StatusBrush = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3));
                break;
        }

        HasEngine = true;
    }

    private string BuildAudioSummary()
    {
        if (!Settings.CaptureSystemAudio && !Settings.CaptureMicrophone)
        {
            return "未启用音频";
        }
        var parts = new List<string>();
        if (Settings.CaptureSystemAudio) parts.Add($"系统声音 {Settings.SystemAudioVolume}%");
        if (Settings.CaptureMicrophone) parts.Add($"麦克风 {Settings.MicrophoneVolume}%");
        return string.Join(" + ", parts);
    }
}
