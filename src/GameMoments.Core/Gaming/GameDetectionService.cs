using System.Diagnostics;
using System.Runtime.InteropServices;
using GameMoments.Core.Configuration;
using GameMoments.Core.Logging;

namespace GameMoments.Core.Gaming;

/// <summary>一次进程扫描中发现的游戏。</summary>
public sealed record DetectedGame(
    string GameId,
    string DisplayName,
    string ProcessName,
    string ExecutablePath,
    string? WindowTitle,
    bool IsForeground,
    DateTime FirstSeenUtc,
    bool SupportsAutomaticEvents,
    string SupportText)
{
    public string ProcessDisplay => string.IsNullOrWhiteSpace(ExecutablePath)
        ? ProcessName + ".exe"
        : Path.GetFileName(ExecutablePath);

    public string StateText => IsForeground ? "前台运行" : "后台运行";
}

/// <summary>
/// 游戏自动检测：定期枚举进程，按内置目录与用户自定义 exe 列表匹配。
/// 只读取进程名/路径/窗口标题，不做任何注入或内存读取。
/// </summary>
public sealed class GameDetectionService : IDisposable
{
    private readonly AppLogger _logger;
    private readonly Func<AppSettings> _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, DetectedGame> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Task? _loop;
    private bool _disposed;

    public GameDetectionService(AppLogger logger, Func<AppSettings> settings)
    {
        _logger = logger;
        _settings = settings;
    }

    public event Action<DetectedGame>? GameStarted;
    public event Action<DetectedGame>? GameStopped;
    public event Action? Changed;

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);

    public bool IsRunning => _loop is { IsCompleted: false };

    public IReadOnlyList<DetectedGame> ActiveGames
    {
        get
        {
            lock (_gate) return _active.Values.OrderBy(g => g.DisplayName).ToArray();
        }
    }

    public void Start()
    {
        if (_disposed || IsRunning) return;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.Info("检测", $"游戏自动检测已启动（每 {Interval.TotalSeconds:0.#} 秒扫描一次进程）");
    }

    public void Stop()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // 忽略取消异常。
        }
        lock (_gate) _active.Clear();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Scan();
                }
                catch (Exception ex)
                {
                    _logger.Warn("检测", "扫描游戏进程失败", ex.Message);
                }

                await Task.Delay(Interval, token);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            _logger.Error("检测", "游戏检测循环异常退出", ex);
        }
    }

    /// <summary>立即扫描一次并触发启停事件；返回当前检测到的游戏。</summary>
    public IReadOnlyList<DetectedGame> Scan()
    {
        var settings = _settings();
        var foregroundPid = ForegroundProcessId();
        var found = new Dictionary<string, DetectedGame>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch
                {
                    continue;
                }

                var definition = GameCatalog.Match(name, null, settings.CustomGameExecutables);
                if (definition is null) continue;
                if (found.ContainsKey(definition.Id)) continue;

                var path = string.Empty;
                try
                {
                    path = process.MainModule?.FileName ?? string.Empty;
                }
                catch
                {
                    // 权限不足（例如以管理员运行的游戏）时只保留进程名。
                }

                string? title = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(process.MainWindowTitle)) title = process.MainWindowTitle;
                }
                catch
                {
                    // 某些进程读取窗口标题会抛异常。
                }

                var firstSeen = _active.TryGetValue(definition.Id, out var previous) && previous is not null
                    ? previous.FirstSeenUtc
                    : DateTime.UtcNow;

                found[definition.Id] = new DetectedGame(
                    definition.Id,
                    definition.DisplayName,
                    GameCatalog.NormalizeExecutableName(name),
                    path,
                    title,
                    foregroundPid is not null && process.Id == foregroundPid,
                    firstSeen,
                    definition.SupportsAutomaticEvents,
                    definition.SupportText);
            }
            catch
            {
                // 单个进程访问失败不影响整体扫描。
            }
            finally
            {
                process.Dispose();
            }
        }

        var started = new List<DetectedGame>();
        var stopped = new List<DetectedGame>();
        var changed = false;

        lock (_gate)
        {
            foreach (var pair in found)
            {
                if (!_active.ContainsKey(pair.Key)) started.Add(pair.Value);
                else if (_active[pair.Key].IsForeground != pair.Value.IsForeground
                         || _active[pair.Key].WindowTitle != pair.Value.WindowTitle)
                {
                    changed = true;
                }
            }

            foreach (var pair in _active)
            {
                if (!found.ContainsKey(pair.Key)) stopped.Add(pair.Value);
            }

            _active.Clear();
            foreach (var pair in found) _active[pair.Key] = pair.Value;
        }

        foreach (var game in started)
        {
            _logger.Info("检测", $"检测到游戏启动：{game.DisplayName}", $"{game.ProcessDisplay} · {game.StateText}");
            TryRaise(GameStarted, game);
        }

        foreach (var game in stopped)
        {
            _logger.Info("检测", $"检测到游戏退出：{game.DisplayName}", game.ProcessDisplay);
            TryRaise(GameStopped, game);
        }

        if (changed || started.Count > 0 || stopped.Count > 0) TryRaise(Changed);

        return found.Values.OrderBy(g => g.DisplayName).ToArray();
    }

    private void TryRaise(Action<DetectedGame>? handler, DetectedGame game)
    {
        try
        {
            handler?.Invoke(game);
        }
        catch (Exception ex)
        {
            _logger.Warn("检测", "游戏检测事件处理异常", ex.Message);
        }
    }

    private void TryRaise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch
        {
            // 订阅者异常不影响检测。
        }
    }

    private static int? ForegroundProcessId()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;
            if (GetWindowThreadProcessId(window, out var pid) == 0) return null;
            return pid == 0 ? null : (int)pid;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
    }
}
