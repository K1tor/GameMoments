using System.Globalization;
using GameMoments.Core.Audio;
using GameMoments.Core.Clips;
using GameMoments.Core.Configuration;
using GameMoments.Core.Logging;
using GameMoments.Core.Media;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Capture;

public enum CaptureState
{
    Idle,
    Starting,
    Replaying,
    Recording,
    Stopping,
    Faulted,
}

public sealed record SaveOutcome(bool Success, ClipRecord? Clip, string? Error)
{
    public static SaveOutcome Ok(ClipRecord clip) => new(true, clip, null);
    public static SaveOutcome Fail(string error) => new(false, null, error);
}

/// <summary>
/// 捕获引擎：启动 ffmpeg 分段捕获 → 维护 1 秒分段环形缓存 → 响应保存请求 →
/// 组装片段并写入片段库。所有状态变化都通过 <see cref="Changed"/> 通知界面。
/// </summary>
public sealed class CaptureEngine : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly AppLogger _logger;
    private readonly FfmpegTools _tools;
    private readonly FfmpegCapabilities _capabilities;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private AppSettings _settings = new();
    private DisplayInfo _display = new(0, string.Empty, string.Empty, new PixelRect(0, 0, 0, 0), true);
    private ClipLibrary? _library;
    private ClipAssembler? _assembler;
    private FfmpegProcess? _process;
    private SegmentRing? _ring;
    private AudioEngine? _audio;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private string? _sessionDirectory;
    private DateTime? _manualStartUtc;
    private DateTime _sessionStartUtc;
    private long _fpsBits;
    private long _speedBits;
    private long _frameBits;
    private long _bitrateBits;
    private int _pendingSaves;
    private volatile bool _stopping;
    private int _savedCount;

    public CaptureEngine(AppPaths paths, AppLogger logger, FfmpegTools tools, FfmpegCapabilities capabilities)
    {
        _paths = paths;
        _logger = logger;
        _tools = tools;
        _capabilities = capabilities;
    }

    public event Action? Changed;

    public CaptureState State { get; private set; } = CaptureState.Idle;
    public string? LastError { get; private set; }
    public string StatusMessage { get; private set; } = "未开始捕获";
    public CaptureProfile? Profile { get; private set; }
    public DisplayInfo? Display => State == CaptureState.Idle ? null : _display;
    public SegmentRing? Ring => _ring;
    public int PendingSaves => Volatile.Read(ref _pendingSaves);
    public int SavedCount => Volatile.Read(ref _savedCount);
    public string? LastSavedPath { get; private set; }
    public DateTime? ManualStartUtc => _manualStartUtc;
    public bool IsManualRecording => _manualStartUtc.HasValue;
    public bool IsRunning => State is CaptureState.Replaying or CaptureState.Recording or CaptureState.Starting;
    public DateTime SessionStartUtc => _sessionStartUtc;
    public string? SessionDirectory => _sessionDirectory;

    public double CacheSeconds => _ring?.CachedSeconds ?? 0;
    public long CacheBytes => _ring?.TotalBytes ?? 0;
    public double CapturedFps => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _fpsBits));
    public double Speed => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _speedBits));
    public long Frames => Interlocked.Read(ref _frameBits);
    public long BitrateKbps => Interlocked.Read(ref _bitrateBits);

    public string AudioSummary
    {
        get
        {
            if (_audio is null) return "无音频";
            var tracks = _audio.Tracks;
            if (tracks.Count == 0) return "无音频";
            return string.Join(" + ", tracks.Select(t =>
                t.Fault is null ? $"{t.Label}（{t.Volume * 100:0}%）" : $"{t.Label}（异常）"));
        }
    }

    public string? AudioWarning => _audio?.FirstFault ?? _audio?.Warnings.FirstOrDefault();

    public async Task<(bool Success, string? Error)> StartAsync(
        AppSettings settings,
        DisplayInfo display,
        ClipLibrary library,
        ClipAssembler assembler,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning) return (false, "捕获已经在运行。");

            State = CaptureState.Starting;
            LastError = null;
            StatusMessage = "正在检测捕获与编码组合…";
            Notify();

            if (_process is not null)
            {
                try
                {
                    await _process.DisposeAsync();
                }
                catch
                {
                    // 忽略上一次会话的清理异常。
                }
                _process = null;
            }
            _audio?.Dispose();
            _audio = null;

            _settings = settings.Clone();
            _display = display;
            _library = library;
            _assembler = assembler;
            _sessionStartUtc = DateTime.UtcNow;
            _stopping = false;

            var saveFolder = ResolveSaveFolder(_settings);
            var probeDirectory = Path.Combine(_paths.TempDirectory, "probe");
            if (!AppPaths.CanWrite(probeDirectory, out var writeError))
            {
                return FailStart(writeError ?? "临时目录不可写，请检查权限或更换数据目录。");
            }
            if (!AppPaths.CanWrite(saveFolder, out var saveError))
            {
                return FailStart(saveError ?? "保存目录不可写，请在设置中更换目录。");
            }
            if (AppPaths.GetFreeBytes(saveFolder) is long freeSpace && freeSpace < 1L * 1024 * 1024 * 1024)
            {
                StatusMessage = $"保存目录剩余空间较少（{AppPaths.FormatBytes(freeSpace)}），保存可能失败。";
            }

            string detail = string.Empty;
            var profile = await Task.Run(
                () => CaptureProbe.Select(_tools, _capabilities, _settings, display, _logger, out detail, cancellationToken),
                cancellationToken);
            if (profile is null)
            {
                return FailStart($"没有可用的捕获/编码组合。{Environment.NewLine}{detail}");
            }
            Profile = profile;
            StatusMessage = $"已选定 {profile.DisplayName}（{display.Label}），正在启动…";
            Notify();

            var sessionId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            _sessionDirectory = Path.Combine(_paths.TempDirectory, "sessions", sessionId);
            Directory.CreateDirectory(_sessionDirectory);

            _ring = new SegmentRing(_sessionDirectory)
            {
                ReplaySeconds = _settings.ReplaySeconds,
                MaxBytes = Math.Max(1, _settings.MaxCacheSizeGb) * 1024L * 1024 * 1024,
            };

            _audio = new AudioEngine();
            try
            {
                _audio.Start(_settings, _paths, _logger);
            }
            catch (Exception ex)
            {
                _logger.Error("音频", "音频采集启动失败，将只录制视频。", ex);
            }

            var arguments = FfmpegCommands.Record(profile, _settings, display, _sessionDirectory);
            var process = FfmpegProcess.Start(_tools.FfmpegPath, arguments, _sessionDirectory);
            _process = process;
            process.ProgressLine += OnProgressLine;
            process.ErrorLine += line => _logger.Debug("ffmpeg", line);
            _ = ObserveProcessExitAsync(process);

            _monitorCts = new CancellationTokenSource();
            _monitorTask = Task.Run(() => MonitorAsync(_monitorCts.Token));

            // 等待第一段真正落盘，确认捕获确实在工作。
            var deadline = DateTime.UtcNow.AddSeconds(12);
            var firstSegment = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(150, cancellationToken);
                _ring.Scan();
                if (_ring.NewestUtc is not null)
                {
                    firstSegment = true;
                    break;
                }
                if (process.HasExited)
                {
                    break;
                }
            }

            if (!firstSegment)
            {
                var tail = FirstLines(process.ErrorTail, 4);
                return FailStart($"捕获进程没有生成视频分段。{(string.IsNullOrWhiteSpace(tail) ? string.Empty : Environment.NewLine + tail)}");
            }

            State = CaptureState.Replaying;
            StatusMessage = "回放缓存已就绪，按快捷键即可保存精彩时刻。";
            if (_audio is { Tracks.Count: > 0 })
            {
                StatusMessage += $" 音频：{AudioSummary}";
            }
            else if (_audio is { Warnings.Count: > 0 })
            {
                StatusMessage += " 音频未录制：" + string.Join("；", _audio.Warnings);
            }

            _logger.Info("捕获", "回放缓存已启动", $"{profile.DisplayName} · {display.Label} · {_settings.ReplaySeconds}s");
            Notify();
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            await StopInternalAsync();
            return (false, "启动已取消。");
        }
        catch (Exception ex)
        {
            _logger.Error("捕获", "启动捕获失败", ex);
            return FailStart($"启动捕获失败：{ex.Message}");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private (bool Success, string? Error) FailStart(string error)
    {
        LastError = error;
        State = CaptureState.Faulted;
        StatusMessage = "捕获启动失败";
        _logger.Error("捕获", error);
        _ = StopInternalAsync(resetUi: false);
        Notify();
        return (false, error);
    }

    public async Task StopAsync(bool saveManualRecording = true)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (State is CaptureState.Idle) return;
            State = CaptureState.Stopping;
            StatusMessage = "正在停止捕获…";
            Notify();

            if (saveManualRecording && IsManualRecording)
            {
                try
                {
                    await StopManualRecordingAsync();
                }
                catch (Exception ex)
                {
                    _logger.Warn("捕获", "停止前保存完整录制失败", ex.Message);
                }
            }

            await StopInternalAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopInternalAsync(bool resetUi = true)
    {
        _stopping = true;
        _manualStartUtc = null;

        try
        {
            _monitorCts?.Cancel();
            if (_monitorTask is not null)
            {
                try
                {
                    await _monitorTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                    // 忽略监控循环退出异常。
                }
            }
        }
        finally
        {
            _monitorCts?.Dispose();
            _monitorCts = null;
            _monitorTask = null;
        }

        try
        {
            _audio?.Stop();
        }
        catch (Exception ex)
        {
            _logger.Warn("音频", "停止音频采集失败", ex.Message);
        }

        if (_process is not null)
        {
            try
            {
                await _process.StopGracefullyAsync(TimeSpan.FromSeconds(6));
            }
            catch (Exception ex)
            {
                _logger.Warn("捕获", "停止 ffmpeg 失败", ex.Message);
            }
            _process.ProgressLine -= OnProgressLine;
            await _process.DisposeAsync();
            _process = null;
        }

        _ring?.MarkAllClosed();

        // 等所有正在进行的保存完成后再删除会话分段。
        try
        {
            await _saveGate.WaitAsync(TimeSpan.FromSeconds(30));
            _saveGate.Release();
        }
        catch
        {
            // 保存超时则保留临时目录，避免删除正在使用的文件。
        }

        if (_audio is not null)
        {
            _audio.Dispose();
            _audio = null;
        }

        if (_sessionDirectory is not null && Directory.Exists(_sessionDirectory))
        {
            try
            {
                Directory.Delete(_sessionDirectory, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.Warn("捕获", $"清理会话目录失败：{ex.Message}", _sessionDirectory);
            }
        }

        _sessionDirectory = null;
        _ring = null;
        Profile = null;
        if (resetUi)
        {
            State = CaptureState.Idle;
            StatusMessage = "已停止捕获";
        }
        Notify();
        _logger.Info("捕获", "捕获已停止");
    }

    public async Task<(bool Success, string? Error)> StartManualRecordingAsync()
    {
        if (State != CaptureState.Replaying)
        {
            return (false, State == CaptureState.Recording ? "已经在完整录制中。" : "捕获尚未就绪。");
        }

        _manualStartUtc = DateTime.UtcNow;
        if (_ring is not null)
        {
            _ring.RetainAll = true;
        }
        State = CaptureState.Recording;
        StatusMessage = "正在完整录制，再次按快捷键停止并保存。";
        _logger.Info("录制", "开始完整录制");
        Notify();
        await Task.CompletedTask;
        return (true, null);
    }

    public async Task<SaveOutcome> StopManualRecordingAsync(string? title = null, GameMoments.Core.Gaming.GameClipTag? tag = null)
    {
        if (!IsManualRecording || _manualStartUtc is null)
        {
            return SaveOutcome.Fail("当前没有进行中的完整录制。");
        }

        var start = _manualStartUtc.Value;
        _manualStartUtc = null;
        if (_ring is not null) _ring.RetainAll = false;
        State = CaptureState.Replaying;

        var outcome = await SaveCoreAsync(start, DateTime.UtcNow, 0, manual: true, title, tag);
        return outcome;
    }

    public async Task<SaveOutcome> SaveReplayAsync(string? title = null, GameMoments.Core.Gaming.GameClipTag? tag = null)
    {
        if (State == CaptureState.Recording)
        {
            return SaveOutcome.Fail("正在完整录制中。请先按录制快捷键停止录制，会自动保存。");
        }
        if (State != CaptureState.Replaying)
        {
            return SaveOutcome.Fail("捕获尚未就绪，暂时不能保存。");
        }

        var replay = Math.Max(5, _settings.ReplaySeconds);
        var trigger = DateTime.UtcNow;
        return await SaveCoreAsync(trigger.AddSeconds(-replay), trigger, Math.Max(0, _settings.PostTriggerSeconds), manual: false, title, tag);
    }

    /// <summary>
    /// 按事件时间点保存片段：保留事件前 <paramref name="preSeconds"/> 秒、事件后继续录制
    /// <paramref name="postSeconds"/> 秒再裁剪。供智能录制的事件规则使用。
    /// </summary>
    public async Task<SaveOutcome> SaveEventClipAsync(
        DateTime eventUtc,
        int preSeconds,
        int postSeconds,
        string? title,
        GameMoments.Core.Gaming.GameClipTag? tag,
        bool allowDuringRecording = false)
    {
        if (State is not (CaptureState.Replaying or CaptureState.Recording))
        {
            return SaveOutcome.Fail("捕获尚未就绪，暂时不能保存事件片段。");
        }
        if (State == CaptureState.Recording && !allowDuringRecording)
        {
            return SaveOutcome.Fail("正在完整录制中，无法单独保存事件片段。");
        }

        var pre = Math.Clamp(preSeconds, 3, 300);
        var post = Math.Clamp(postSeconds, 0, 60);
        return await SaveCoreAsync(eventUtc.AddSeconds(-pre), eventUtc, post, manual: false, title, tag);
    }

    private async Task<SaveOutcome> SaveCoreAsync(
        DateTime requestedStartUtc,
        DateTime triggerUtc,
        int postSeconds,
        bool manual,
        string? title,
        GameMoments.Core.Gaming.GameClipTag? tag = null)
    {
        if (Interlocked.Increment(ref _pendingSaves) > Math.Max(1, _settings.MaxQueuedSaves))
        {
            Interlocked.Decrement(ref _pendingSaves);
            return SaveOutcome.Fail($"同时保存的片段过多（上限 {_settings.MaxQueuedSaves}），请稍后再试。");
        }

        await _saveGate.WaitAsync();
        try
        {
            if (_ring is null || _assembler is null || _library is null)
            {
                return SaveOutcome.Fail("捕获已经停止，无法保存。");
            }

            StatusMessage = "正在保存精彩时刻，等待最后的分段写完…";
            Notify();

            var endTarget = triggerUtc.AddSeconds(postSeconds);
            var waitDeadline = DateTime.UtcNow.AddSeconds(postSeconds + 10);
            while (DateTime.UtcNow < waitDeadline)
            {
                _ring.Scan();
                if (_ring.HasSegmentAfter(endTarget)) break;
                if (State is CaptureState.Idle or CaptureState.Faulted or CaptureState.Stopping) break;
                await Task.Delay(100);
            }
            _ring.Scan();

            var segments = _ring.Select(requestedStartUtc, endTarget);
            if (segments.Count == 0)
            {
                return SaveOutcome.Fail(
                    $"缓存中没有覆盖该时间段的视频分段（当前缓存约 {_ring.CachedSeconds:0.0} 秒）。请等缓存建立后再试。");
            }

            var clipStart = segments[0].CreatedUtc;
            var clipDuration = segments.Count * 1.0;
            using var pin = _ring.Pin(segments);

            var audioSources = new List<AudioMixSource>();
            var audioPins = new List<IDisposable>();
            if (_audio is not null)
            {
                foreach (var track in _audio.Tracks)
                {
                    try
                    {
                        audioPins.Add(track.Pin(clipStart, clipStart.AddSeconds(clipDuration)));
                        var chunks = track.Chunks
                            .Where(c => c.EndUtc >= clipStart && c.StartUtc <= clipStart.AddSeconds(clipDuration))
                            .Select(c => c.Path)
                            .ToList();
                        if (chunks.Count == 0) continue;
                        audioSources.Add(new AudioMixSource(
                            track.Label,
                            chunks,
                            (clipStart - track.SessionStartUtc).TotalSeconds,
                            clipDuration,
                            track.Volume));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("保存", $"{track.Label} 音频 pin 失败，将不混入该轨", ex.Message);
                    }
                }
            }

            try
            {
                var request = new AssemblyRequest(segments, clipStart, clipDuration, audioSources, manual, title, tag);
                var clip = await _assembler.AssembleAsync(request, _settings, Profile ?? new CaptureProfile(CaptureSource.Gdigrab, "libx264", _settings.Fps));
                _library.Add(clip);
                LastSavedPath = clip.FilePath;
                Interlocked.Increment(ref _savedCount);

                StatusMessage = $"已保存：{clip.FileName}（{clip.DurationText}，{clip.SizeText}）";
                _logger.Info("保存", StatusMessage);
                Notify();
                return SaveOutcome.Ok(clip);
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                _logger.Error("保存", "保存片段失败", ex);
                StatusMessage = $"保存失败：{message}";
                Notify();
                return SaveOutcome.Fail(message);
            }
            finally
            {
                foreach (var audioPin in audioPins)
                {
                    try
                    {
                        audioPin.Dispose();
                    }
                    catch
                    {
                        // 忽略。
                    }
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSaves);
            _saveGate.Release();
            if ((State is CaptureState.Replaying or CaptureState.Recording)
                && StatusMessage.StartsWith("正在保存", StringComparison.Ordinal))
            {
                StatusMessage = State == CaptureState.Recording
                    ? "正在完整录制，再次按快捷键停止并保存。"
                    : "回放缓存已就绪，按快捷键即可保存精彩时刻。";
                Notify();
            }
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings.Clone();
        if (_ring is not null)
        {
            _ring.ReplaySeconds = _settings.ReplaySeconds;
            _ring.MaxBytes = Math.Max(1, _settings.MaxCacheSizeGb) * 1024L * 1024 * 1024;
        }
        _audio?.ApplyVolumes(_settings);
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                if (_ring is null) break;
                _ring.Scan();
                if (State == CaptureState.Recording && _manualStartUtc is null) break;

                // 音频与视频使用同一回放窗口，额外多保留 10 秒余量。
                var cutoff = DateTime.UtcNow.AddSeconds(-(_settings.ReplaySeconds + Math.Max(0, _settings.PostTriggerSeconds) + 15));
                _audio?.Prune(cutoff);

                if (_manualStartUtc is { } manualStart && _ring.TotalBytes > _ring.MaxBytes)
                {
                    _logger.Warn("录制", "完整录制的缓存达到上限，自动停止录制并保存。");
                    StatusMessage = "完整录制达到缓存上限，正在停止并保存…";
                    Notify();
                    await StopManualRecordingAsync();
                    continue;
                }

                Notify();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出。
        }
        catch (Exception ex)
        {
            _logger.Error("捕获", "监控循环异常退出", ex);
        }
    }

    private async Task ObserveProcessExitAsync(FfmpegProcess process)
    {
        int code;
        try
        {
            code = await process.WaitForExitAsync();
        }
        catch
        {
            return;
        }

        if (_stopping || State is CaptureState.Stopping or CaptureState.Idle) return;

        var tail = FirstLines(process.ErrorTail, 5);
        LastError = $"视频捕获进程意外退出（代码 {code}）。{(string.IsNullOrWhiteSpace(tail) ? string.Empty : Environment.NewLine + tail)}";
        State = CaptureState.Faulted;
        StatusMessage = "捕获已中断";
        _logger.Error("捕获", LastError);
        try
        {
            _monitorCts?.Cancel();
        }
        catch
        {
            // 忽略。
        }
        try
        {
            _audio?.Stop();
        }
        catch
        {
            // 忽略。
        }
        Notify();
    }

    private void OnProgressLine(string line)
    {
        var index = line.IndexOf('=');
        if (index <= 0) return;
        var key = line[..index].Trim();
        var value = line[(index + 1)..].Trim();
        switch (key)
        {
            case "fps":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
                {
                    Interlocked.Exchange(ref _fpsBits, BitConverter.DoubleToInt64Bits(fps));
                }
                break;
            case "speed":
                if (value.EndsWith('x')) value = value[..^1];
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                {
                    Interlocked.Exchange(ref _speedBits, BitConverter.DoubleToInt64Bits(speed));
                }
                break;
            case "frame":
                if (long.TryParse(value, out var frames)) Interlocked.Exchange(ref _frameBits, frames);
                break;
            case "bitrate":
                if (value.EndsWith("kbits/s", StringComparison.OrdinalIgnoreCase))
                {
                    value = value[..^7].Trim();
                }
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bitrate))
                {
                    Interlocked.Exchange(ref _bitrateBits, (long)bitrate);
                }
                break;
        }
    }

    private string ResolveSaveFolder(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.SaveFolder) ? _paths.DefaultSaveDirectory : settings.SaveFolder;

    private static string FirstLines(string text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(Environment.NewLine, lines.Take(count));
    }

    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 界面订阅者异常不应影响采集。
        }
    }

    /// <summary>停止捕获并释放资源（供命令行/退出流程使用）。</summary>
    public async Task ShutdownAsync()
    {
        await StopAsync(saveManualRecording: false);
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(saveManualRecording: false);
        }
        catch
        {
            // 忽略退出时的清理异常。
        }
        _lifecycle.Dispose();
        _saveGate.Dispose();
    }
}
