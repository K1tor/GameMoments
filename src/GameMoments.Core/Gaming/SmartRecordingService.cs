using GameMoments.Core.Capture;
using GameMoments.Core.Clips;
using GameMoments.Core.Configuration;
using GameMoments.Core.Logging;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Gaming;

/// <summary>
/// 智能录制编排：检测游戏进程 → 按游戏配置启动捕获/整局录制 → 接收事件源推送 →
/// 按事件规则自动剪辑（或只加书签）→ 游戏退出时安全收尾。
/// </summary>
public sealed class SmartRecordingService : IDisposable
{
    private readonly AppLogger _logger;
    private readonly AppPaths _paths;
    private readonly ClipLibrary _library;
    private readonly Func<AppSettings> _settings;
    private readonly Func<CaptureEngine?> _engine;
    private readonly Func<ClipAssembler?> _assembler;

    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly GsiConfigInstaller _installer;

    private GameDetectionService? _detection;
    private CancellationTokenSource? _wholeStopCts;
    private bool _captureStartedBySmart;
    private bool _disposed;
    private string? _lastError;
    private GsiConfigStatus? _cs2Config;

    public SmartRecordingService(
        AppLogger logger,
        AppPaths paths,
        ClipLibrary library,
        Func<AppSettings> settingsProvider,
        Func<CaptureEngine?> engineProvider,
        Func<ClipAssembler?> assemblerProvider)
    {
        _logger = logger;
        _paths = paths;
        _library = library;
        _settings = settingsProvider;
        _engine = engineProvider;
        _assembler = assemblerProvider;

        Events = new EventEngine(logger, settingsProvider, () => _engine()?.IsRunning == true);
        Events.SaveRequested += OnSaveRequested;
        Events.RecordAdded += record => TryRaise(() => EventRecorded?.Invoke(record));
        Events.RecordUpdated += record => TryRaise(() => EventRecordUpdated?.Invoke(record));

        Server = new GsiServer(logger);
        Server.PayloadReceived += OnPayloadReceived;
        Cs2 = new Cs2GsiAdapter(logger);
        _installer = new GsiConfigInstaller(logger);
    }

    public EventEngine Events { get; }

    public GsiServer Server { get; }

    public Cs2GsiAdapter Cs2 { get; }

    public GameDetectionService? Detection => _detection;

    /// <summary>智能录制总开关是否已生效（检测循环正在运行）。</summary>
    public bool IsRunning => _detection is { IsRunning: true };

    public string? LastError
    {
        get
        {
            lock (_stateGate) return _lastError;
        }
    }

    public int GsiPort => Server.Port;

    public string GsiEndpointText => Server.IsRunning ? $"http://127.0.0.1:{Server.Port}/" : "未监听";

    public GsiConfigStatus? Cs2ConfigStatus
    {
        get
        {
            lock (_stateGate) return _cs2Config;
        }
    }

    public IReadOnlyList<DetectedGame> DetectedGames => _detection?.ActiveGames ?? Array.Empty<DetectedGame>();

    /// <summary>当前游戏：优先前台运行的游戏。</summary>
    public DetectedGame? CurrentGame =>
        DetectedGames.FirstOrDefault(g => g.IsForeground) ?? DetectedGames.FirstOrDefault();

    public bool IsWholeRecording { get; private set; }

    public string? WholeRecordingGameName { get; private set; }

    public string DetectionText
    {
        get
        {
            if (!IsRunning) return "智能录制未运行";
            var games = DetectedGames;
            if (games.Count == 0) return "未检测到游戏（每 2 秒扫描一次进程）";
            return "已检测到：" + string.Join("、", games.Select(g => g.DisplayName));
        }
    }

    /// <summary>整局录制的磁盘占用估算（按当前画质档位的码率与设定时长折算）。</summary>
    public string WholeRecordingEstimateText
    {
        get
        {
            var settings = _settings();
            var minutes = settings.WholeRecordingMinutes;
            if (minutes <= 0) return "整局录制：不限时长（按暂停时实际大小占用磁盘）";

            var kbps = BitrateKbps(settings.QualityPreset);
            var bytes = (long)(kbps * 1000d / 8d * minutes * 60d);
            return $"整局录制：上限 {minutes} 分钟，按 {kbps / 1000.0:0.#} Mbps 估算约 {Platform.AppPaths.FormatBytes(bytes)}";
        }
    }

    private static int BitrateKbps(string? qualityPreset) => (qualityPreset ?? "high").ToLowerInvariant() switch
    {
        "low" => 8000,
        "medium" => 14000,
        _ => 24000,
    };

    public event Action? Changed;

    public event Action<SmartEventRecord>? EventRecorded;

    public event Action<SmartEventRecord>? EventRecordUpdated;

    /// <summary>面向用户的提示（保存成功/失败、事件源连通等）。</summary>
    public event Action<string>? Notice;

    private readonly object _stateGate = new();

    public async Task<(bool Success, string? Error)> StartAsync()
    {
        if (IsRunning) return (true, null);

        var settings = _settings();
        if (!settings.SmartRecordingEnabled)
        {
            return (false, "智能录制总开关未开启。");
        }

        var error = await EnsureEventSourceAsync(settings);
        if (error is not null)
        {
            SetError(error);
        }

        _detection = new GameDetectionService(_logger, _settings);
        _detection.GameStarted += OnGameStarted;
        _detection.GameStopped += OnGameStopped;
        _detection.Changed += () => TryRaise(() => Changed?.Invoke());
        _detection.Start();

        _logger.Info("智能录制", "智能录制已启动");
        TryRaise(() => Changed?.Invoke());

        // 处理“启动前游戏已经在跑”的情况。
        await SyncAsync();
        return (true, null);
    }

    public async Task StopAsync()
    {
        if (_detection is not null)
        {
            _detection.GameStarted -= OnGameStarted;
            _detection.GameStopped -= OnGameStopped;
            _detection.Dispose();
            _detection = null;
        }

        try
        {
            _wholeStopCts?.Cancel();
            _wholeStopCts?.Dispose();
            _wholeStopCts = null;
        }
        catch
        {
            // 忽略。
        }

        Server.Stop();
        Cs2.Reset();

        if (IsWholeRecording)
        {
            await StopWholeRecordingAsync("智能录制已停止");
        }

        _logger.Info("智能录制", "智能录制已停止");
        TryRaise(() => Changed?.Invoke());
    }

    /// <summary>立即扫描一次进程并按结果启动/停止捕获（用于界面手动刷新）。</summary>
    public async Task SyncAsync()
    {
        if (_detection is null) return;

        try
        {
            _detection.Scan();
        }
        catch (Exception ex)
        {
            _logger.Warn("智能录制", "扫描进程失败", ex.Message);
        }

        await HandleIdleStateAsync();
        TryRaise(() => Changed?.Invoke());
    }

    /// <summary>重新监听端口并（可选）重新安装 GSI 配置，供界面“修复事件源”按钮调用。</summary>
    public async Task<(bool Success, string Message)> RepairEventSourceAsync(bool reinstallConfig = true)
    {
        var settings = _settings();
        var error = await EnsureEventSourceAsync(settings, forceReinstall: reinstallConfig);
        TryRaise(() => Changed?.Invoke());
        if (error is not null)
        {
            SetError(error);
            return (false, error);
        }

        return (true, $"事件源已就绪：{GsiEndpointText}");
    }

    /// <summary>把书签事件（或已合并的事件）从回放缓存补存为片段。</summary>
    public async Task<(bool Success, string Message)> SaveRecordFromCacheAsync(SmartEventRecord record)
    {
        if (_disposed) return (false, "程序正在退出，无法保存。");

        await _saveGate.WaitAsync();
        try
        {
            var engine = _engine();
            if (engine is null || !engine.IsRunning)
            {
                return (false, "捕获当前没有运行，无法从缓存补存。");
            }

            var decision = new EventDecision(
                record.Event,
                EventOutcome.Saved,
                "手动从回放缓存保存。",
                record.PreSeconds,
                record.PostSeconds,
                BuildTitle(record.Event));

            var tag = new GameClipTag(record.Event.GameName, record.Event.Kind, record.Event.Source);
            var outcome = await engine.SaveEventClipAsync(
                record.Event.OccurredUtc,
                record.PreSeconds,
                record.PostSeconds,
                decision.ClipTitle,
                tag,
                allowDuringRecording: engine.IsManualRecording);

            if (outcome.Success && outcome.Clip is not null)
            {
                Events.MarkSaved(record.Event.GameId, record.Event.Kind, DateTime.UtcNow);
                Events.AttachClip(record, outcome.Clip.FilePath, outcome.Clip.FileName, true, "已从回放缓存补存。");
                Notify($"已保存事件片段：{outcome.Clip.FileName}");
                TryRaise(() => Changed?.Invoke());
                return (true, outcome.Clip.FileName);
            }

            var failure = outcome.Error ?? "保存失败。";
            Events.AttachClip(record, null, null, false, failure);
            TryRaise(() => Changed?.Invoke());
            return (false, failure);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>手动结束当前的整局录制（界面按钮）。</summary>
    public async Task<(bool Success, string Message)> StopWholeRecordingNowAsync()
    {
        if (!IsWholeRecording) return (false, "当前没有进行中的整局录制。");
        await StopWholeRecordingAsync("手动结束整局录制");
        return (true, "整局录制已结束并保存。");
    }

    // ---- 事件源 ----

    private async Task<string?> EnsureEventSourceAsync(AppSettings settings, bool forceReinstall = false)
    {
        if (!Server.IsRunning)
        {
            if (!Server.Start(settings.SmartGsiPort, out var port, out var error))
            {
                return error;
            }

            settings.SmartGsiPort = port;
        }

        var definition = GameCatalog.Find(GameCatalog.Cs2GameId);
        if (definition is null) return null;

        var status = _installer.Query(definition);
        lock (_stateGate) _cs2Config = status;

        if (!settings.InstallGsiConfig) return null;

        if (definition.SupportsGsiConfig)
        {
            if (!status.Installed || forceReinstall || status.ConfiguredPort != Server.Port)
            {
                var result = _installer.Install(definition, Server.Port);
                if (!result.Success && status.Exists && !status.OwnedByGameMoments)
                {
                    _logger.Warn("智能录制", "CS2 的 GSI 配置未安装（存在他人文件）", result.Message);
                }

                lock (_stateGate) _cs2Config = _installer.Query(definition);
            }
        }

        await Task.CompletedTask;
        return null;
    }

    private void OnPayloadReceived(GsiRequest request)
    {
        var settings = _settings();
        if (!settings.SmartRecordingEnabled)
        {
            return;
        }

        if (!string.Equals(request.GameId, GameCatalog.Cs2GameId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Debug("智能录制", "收到未知游戏的状态推送", request.GameId + " " + request.Path);
            return;
        }

        IReadOnlyList<GameEvent> events;
        try
        {
            events = Cs2.ProcessPayload(request.Body, out var error);
            if (error is not null)
            {
                SetError("事件源数据异常：" + error);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.Error("智能录制", "解析游戏状态失败", ex);
            SetError("解析游戏状态失败：" + ex.Message);
            return;
        }

        foreach (var gameEvent in events)
        {
            if (string.Equals(gameEvent.Kind, GameEventKinds.Connected, StringComparison.OrdinalIgnoreCase))
            {
                Notify($"已接入 {gameEvent.GameName} 事件源（{Cs2.SourceLabel}）。");
                continue;
            }

            Events.Evaluate(gameEvent);
        }

        TryRaise(() => Changed?.Invoke());
    }

    private async void OnSaveRequested(EventDecision decision, SmartEventRecord record)
    {
        if (_disposed)
        {
            Events.AttachClip(record, null, null, false, "程序正在退出，事件未保存。");
            return;
        }

        await _saveGate.WaitAsync();
        try
        {
            var engine = _engine();
            if (engine is null || !engine.IsRunning)
            {
                Events.AttachClip(record, null, null, false, "捕获未运行，事件已转为书签。");
                return;
            }

            var tag = new GameClipTag(decision.Event.GameName, decision.Event.Kind, decision.Event.Source);
            var outcome = await engine.SaveEventClipAsync(
                decision.Event.OccurredUtc,
                decision.PreSeconds,
                decision.PostSeconds,
                decision.ClipTitle,
                tag,
                allowDuringRecording: engine.IsManualRecording);

            if (outcome.Success && outcome.Clip is not null)
            {
                Events.MarkSaved(decision.Event.GameId, decision.Event.Kind, DateTime.UtcNow);
                Events.AttachClip(record, outcome.Clip.FilePath, outcome.Clip.FileName, true);
                Notify($"已自动保存 {decision.Event.GameName} · {GameEventKinds.Label(decision.Event.Kind)}：{outcome.Clip.FileName}");
            }
            else
            {
                Events.AttachClip(record, null, null, false, "保存失败：" + (outcome.Error ?? "未知错误"));
            }

            TryRaise(() => Changed?.Invoke());
        }
        catch (Exception ex)
        {
            _logger.Error("智能录制", "自动保存事件片段失败", ex);
            Events.AttachClip(record, null, null, false, "保存异常：" + ex.Message);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    // ---- 游戏启停 ----

    private void OnGameStarted(DetectedGame game)
    {
        _ = Task.Run(() => HandleGameStartedAsync(game));
    }

    private void OnGameStopped(DetectedGame game)
    {
        _ = Task.Run(() => HandleGameStoppedAsync(game));
    }

    private async Task HandleGameStartedAsync(DetectedGame game)
    {
        await _controlGate.WaitAsync();
        try
        {
            var settings = _settings();
            if (!settings.SmartRecordingEnabled) return;

            var config = settings.GetGameConfig(game.GameId);
            if (!config.Enabled)
            {
                _logger.Info("智能录制", $"{game.DisplayName} 已在设置里单独关闭，跳过自动捕获。");
                return;
            }

            if (config.WantsCapture)
            {
                await EnsureCaptureAsync(game, config);
            }

            if (game.SupportsAutomaticEvents)
            {
                await EnsureEventSourceAsync(settings);
                Notify($"{game.DisplayName} 已启动，事件源 {Cs2.SourceLabel} 正在监听 {GsiEndpointText}");
            }
            else
            {
                Notify($"{game.DisplayName} 已启动：只支持自动检测与手动/整局录制，不支持自动事件识别。");
            }

            if (config.WantsWholeRecording)
            {
                await StartWholeRecordingAsync(game);
            }

            TryRaise(() => Changed?.Invoke());
        }
        catch (Exception ex)
        {
            _logger.Error("智能录制", $"处理 {game.DisplayName} 启动失败", ex);
            SetError($"{game.DisplayName} 启动处理失败：{ex.Message}");
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private async Task HandleGameStoppedAsync(DetectedGame game)
    {
        await _controlGate.WaitAsync();
        try
        {
            if (IsWholeRecording && string.Equals(WholeRecordingGameName, game.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                ScheduleWholeRecordingStop(game);
            }

            await RestoreSettingsAfterGameAsync(game);
            await HandleIdleStateAsync();
            TryRaise(() => Changed?.Invoke());
        }
        catch (Exception ex)
        {
            _logger.Error("智能录制", $"处理 {game.DisplayName} 退出失败", ex);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private async Task HandleIdleStateAsync()
    {
        var settings = _settings();
        var games = DetectedGames;
        var anyWantsCapture = games.Any(g =>
        {
            var config = settings.GetGameConfig(g.GameId);
            return config.Enabled && config.WantsCapture;
        });

        if (!anyWantsCapture && _captureStartedBySmart)
        {
            var engine = _engine();
            if (engine is not null && engine.IsRunning)
            {
                if (IsWholeRecording)
                {
                    await StopWholeRecordingAsync("没有正在运行的游戏");
                }

                await engine.StopAsync(saveManualRecording: false);
                _logger.Info("智能录制", "所有游戏已退出，已停止自动捕获。");
            }

            _captureStartedBySmart = false;
            TryRaise(() => Changed?.Invoke());
        }
    }

    private async Task EnsureCaptureAsync(DetectedGame game, GameSettings config)
    {
        var engine = _engine();
        var assembler = _assembler();
        if (engine is null || assembler is null)
        {
            SetError("视频编码器还没准备好（未检测到 ffmpeg），无法自动开始捕获。");
            return;
        }

        var settings = _settings().Clone();
        if (config.ReplaySeconds > 0) settings.ReplaySeconds = config.ReplaySeconds;

        if (engine.IsRunning)
        {
            // 捕获已经在跑：只把回放时长调整为该游戏的配置。
            engine.ApplySettings(settings);
            return;
        }

        var displays = Displays.Enumerate();
        if (displays.Count == 0)
        {
            SetError("没有检测到可捕获的显示器。");
            return;
        }

        var display = displays.FirstOrDefault(d => d.Index == settings.DisplayIndex) ?? displays[0];
        var (success, error) = await engine.StartAsync(settings, display, _library, assembler);
        if (success)
        {
            _captureStartedBySmart = true;
            _logger.Info("智能录制", $"已为 {game.DisplayName} 自动开始回放缓存（{settings.ReplaySeconds} 秒）");
            Notify($"已自动开始捕获：{game.DisplayName}");
        }
        else
        {
            SetError($"自动开始捕获失败：{error}");
        }
    }

    private async Task RestoreSettingsAfterGameAsync(DetectedGame game)
    {
        var engine = _engine();
        if (engine is null || !engine.IsRunning) return;

        var config = _settings().GetGameConfig(game.GameId);
        if (config.ReplaySeconds <= 0) return;

        var settings = _settings().Clone();
        engine.ApplySettings(settings);
    }

    // ---- 整局录制 ----

    private async Task StartWholeRecordingAsync(DetectedGame game)
    {
        var engine = _engine();
        if (engine is null || !engine.IsRunning)
        {
            SetError($"{game.DisplayName} 需要整局录制，但捕获没有启动。");
            return;
        }

        if (engine.IsManualRecording)
        {
            _logger.Info("智能录制", "已有整局录制在进行中，跳过这次自动整局录制。");
            return;
        }

        var settings = _settings();
        var (success, error) = await engine.StartManualRecordingAsync();
        if (!success)
        {
            SetError($"自动开始整局录制失败：{error}");
            return;
        }

        IsWholeRecording = true;
        WholeRecordingGameName = game.DisplayName;
        _logger.Info("智能录制", $"已为 {game.DisplayName} 开始整局录制（单文件上限 {settings.WholeRecordingMinutes} 分钟）");
        Notify($"已自动开始整局录制：{game.DisplayName}");
        TryRaise(() => Changed?.Invoke());
    }

    private void ScheduleWholeRecordingStop(DetectedGame game)
    {
        try
        {
            _wholeStopCts?.Cancel();
            _wholeStopCts?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        var cts = new CancellationTokenSource();
        _wholeStopCts = cts;
        var delay = Math.Max(0, _settings().WholeRecordingStopDelaySeconds);
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token);
                await StopWholeRecordingAsync($"{game.DisplayName} 已退出");
            }
            catch (OperationCanceledException)
            {
                // 游戏又启动了，取消收尾。
            }
        }, cts.Token);
    }

    private async Task StopWholeRecordingAsync(string reason)
    {
        var engine = _engine();
        var name = WholeRecordingGameName ?? "整局录制";
        IsWholeRecording = false;
        WholeRecordingGameName = null;

        if (engine is null || !engine.IsManualRecording)
        {
            TryRaise(() => Changed?.Invoke());
            return;
        }

        var tag = new GameClipTag(name, "whole", "整局自动录制");
        var outcome = await engine.StopManualRecordingAsync($"{name} 整局", tag);
        if (outcome.Success && outcome.Clip is not null)
        {
            Notify($"{reason}，整局录制已保存：{outcome.Clip.FileName}（{outcome.Clip.DurationText}）");
            _logger.Info("智能录制", $"整局录制已保存：{outcome.Clip.FileName}");
        }
        else
        {
            Notify($"{reason}，整局录制保存失败：{outcome.Error}");
        }

        TryRaise(() => Changed?.Invoke());
    }

    private static string BuildTitle(GameEvent gameEvent) =>
        $"{gameEvent.GameName} {GameEventKinds.Label(gameEvent.Kind)} {gameEvent.OccurredUtc.ToLocalTime():HHmmss}";

    private void Notify(string message)
    {
        _logger.Info("智能录制", message);
        try
        {
            Notice?.Invoke(message);
        }
        catch
        {
            // 订阅者异常不影响主流程。
        }
    }

    private void SetError(string message)
    {
        lock (_stateGate) _lastError = message;
        _logger.Warn("智能录制", message);
        TryRaise(() => Changed?.Invoke());
    }

    public void ClearError()
    {
        lock (_stateGate) _lastError = null;
        TryRaise(() => Changed?.Invoke());
    }

    private void TryRaise(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Warn("智能录制", "界面回调异常", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 释放时的异常忽略。
        }

        Server.Dispose();
        _controlGate.Dispose();
        // 注意：_saveGate 不在这里 Dispose。事件保存是异步触发的，退出时可能仍有在途保存
        // 正在 await/Release，Dispose 会让它们抛 ObjectDisposedException。
        // SemaphoreSlim 未使用 AvailableWaitHandle 时不需要显式释放。
    }
}
