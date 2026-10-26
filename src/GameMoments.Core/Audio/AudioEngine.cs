using GameMoments.Core.Configuration;
using GameMoments.Core.Logging;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Audio;

/// <summary>管理系统声音与麦克风两路采集，统一会话起始时间，失败时降级而不是中断视频录制。</summary>
public sealed class AudioEngine : IDisposable
{
    private readonly List<string> _warnings = new();
    private AppLogger? _logger;
    private bool _disposed;

    public AudioTrackRecorder? System { get; private set; }
    public AudioTrackRecorder? Microphone { get; private set; }
    public DateTime? SessionStartUtc { get; private set; }
    public bool IsRunning { get; private set; }
    public string? DirectoryPath { get; private set; }
    public IReadOnlyList<string> Warnings => _warnings;

    public IReadOnlyList<AudioTrackRecorder> Tracks
    {
        get
        {
            var list = new List<AudioTrackRecorder>(2);
            if (System is not null) list.Add(System);
            if (Microphone is not null) list.Add(Microphone);
            return list;
        }
    }

    public void Start(AppSettings settings, AppPaths paths, AppLogger logger)
    {
        if (IsRunning) return;
        _logger = logger;
        _warnings.Clear();
        ReleaseTracks();

        var sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        DirectoryPath = Path.Combine(paths.TempDirectory, "audio", sessionId);
        SessionStartUtc = DateTime.UtcNow;

        if (settings.CaptureSystemAudio)
        {
            StartTrack(AudioDeviceKind.Render, settings.SystemAudioDeviceId, AudioTrackRecorderLabels.System,
                Path.Combine(DirectoryPath, "system"), settings.SystemAudioVolume / 100f);
        }

        if (settings.CaptureMicrophone)
        {
            StartTrack(AudioDeviceKind.Capture, settings.MicrophoneDeviceId, AudioTrackRecorderLabels.Microphone,
                Path.Combine(DirectoryPath, "microphone"), settings.MicrophoneVolume / 100f);
        }

        IsRunning = true;
        var summary = Tracks.Count == 0
            ? "未启用任何音频轨"
            : string.Join("；", Tracks.Select(t => $"{t.Label}：{t.DeviceName}"));
        logger.Info("音频", "音频采集会话已启动", summary);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        foreach (var track in Tracks)
        {
            try
            {
                track.Stop();
            }
            catch (Exception ex)
            {
                _logger?.Warn("音频", $"{track.Label} 停止失败", ex.Message);
            }
        }
        IsRunning = false;
        _logger?.Info("音频", "音频采集已停止", Tracks.Count == 0 ? "无音频轨" : string.Join("；", Tracks.Select(t => $"{t.Label} {t.TotalFrames / (double)WavIO.SampleRate:0.0}s")));
    }

    public void ApplyVolumes(AppSettings settings)
    {
        if (System is not null) System.Volume = settings.SystemAudioVolume / 100f;
        if (Microphone is not null) Microphone.Volume = settings.MicrophoneVolume / 100f;
    }

    public void Prune(DateTime beforeUtc)
    {
        foreach (var track in Tracks) track.Prune(beforeUtc);
    }

    public string? FirstFault => Tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.Fault))?.Fault;

    /// <summary>停止并释放当前会话的采集对象（保留磁盘上的音频分块，直到会话目录被清理）。</summary>
    public void ReleaseTracks()
    {
        if (System is not null)
        {
            System.Dispose();
            System = null;
        }
        if (Microphone is not null)
        {
            Microphone.Dispose();
            Microphone = null;
        }
        IsRunning = false;
    }

    private void StartTrack(AudioDeviceKind kind, string? deviceId, string label, string directory, float volume)
    {
        var device = AudioDeviceService.Resolve(kind, deviceId, out var resolveError);
        if (resolveError is not null)
        {
            _warnings.Add($"{label}：{resolveError}");
            _logger?.Warn("音频", $"{label} 设备解析提示", resolveError);
        }
        if (device is null)
        {
            _warnings.Add($"{label}：没有可用设备，本次不会录制该轨。");
            _logger?.Warn("音频", $"{label} 未启用", "没有可用音频端点。");
            return;
        }

        var deviceName = "未知设备";
        try
        {
            deviceName = device.FriendlyName;
        }
        catch
        {
            // 设备可能已被移除。
        }

        var capture = AudioDeviceService.CreateCapture(kind, device);
        if (capture is null)
        {
            _warnings.Add($"{label}：无法创建采集对象。");
            return;
        }

        try
        {
            var recorder = new AudioTrackRecorder(capture, label, deviceName, directory,
                SessionStartUtc ?? DateTime.UtcNow, _logger!, volume);
            recorder.Start();
            if (kind == AudioDeviceKind.Render) System = recorder;
            else Microphone = recorder;
        }
        catch (Exception ex)
        {
            capture.Dispose();
            _warnings.Add($"{label}：启动失败（{ex.Message}）。本次不会录制该轨。");
            _logger?.Error("音频", $"{label} 启动失败", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        ReleaseTracks();
    }
}
