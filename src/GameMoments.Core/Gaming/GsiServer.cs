using System.Net;
using System.Net.Sockets;
using System.Text;
using GameMoments.Core.Logging;

namespace GameMoments.Core.Gaming;

/// <summary>一次游戏状态推送请求（GSI 的 HTTP POST）。</summary>
public sealed record GsiRequest(
    string GameId,
    string Path,
    string Method,
    string Body,
    string Remote,
    DateTime ReceivedUtc)
{
    public int BodyLength => Body.Length;
}

/// <summary>
/// 极简 HTTP 回环服务器，用于接收游戏的状态推送（Game State Integration）。
/// 直接绑定 127.0.0.1 的 TCP 端口而不是 HttpListener，避免 Windows 上需要 URL ACL/管理员权限。
/// </summary>
public sealed class GsiServer : IDisposable
{
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private const int MaxPortAttempts = 8;

    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _slots = new(24, 24);
    private readonly List<string> _attemptedPorts = new();
    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _requestCount;
    private int _errorCount;
    private DateTime? _lastPayloadUtc;
    private string? _lastPayloadGameId;
    private bool _disposed;

    public GsiServer(AppLogger logger)
    {
        _logger = logger;
    }

    public event Action<GsiRequest>? PayloadReceived;

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    public int RequestCount => Volatile.Read(ref _requestCount);

    public int ErrorCount => Volatile.Read(ref _errorCount);

    public DateTime? LastPayloadUtc
    {
        get
        {
            lock (_gate) return _lastPayloadUtc;
        }
    }

    public string? LastPayloadGameId
    {
        get
        {
            lock (_gate) return _lastPayloadGameId;
        }
    }

    public IReadOnlyList<string> AttemptedPorts
    {
        get
        {
            lock (_attemptedPorts) return _attemptedPorts.ToArray();
        }
    }

    public string? LastError { get; private set; }

    /// <summary>在指定端口（或随后若干端口）启动监听；返回实际使用的端口。</summary>
    public bool Start(int preferredPort, out int port, out string? error)
    {
        port = 0;
        error = null;
        if (_disposed)
        {
            error = "服务已释放。";
            return false;
        }

        if (IsRunning)
        {
            port = Port;
            return true;
        }

        var start = Math.Clamp(preferredPort <= 0 ? 48123 : preferredPort, 1024, 65535);
        for (var offset = 0; offset < MaxPortAttempts && start + offset <= 65535; offset++)
        {
            var candidate = start + offset;
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, candidate);
                listener.Start(64);
            }
            catch (SocketException ex)
            {
                listener?.Stop();
                lock (_attemptedPorts) _attemptedPorts.Add($"{candidate}: {ex.SocketErrorCode}");
                continue;
            }
            catch (Exception ex)
            {
                listener?.Stop();
                lock (_attemptedPorts) _attemptedPorts.Add($"{candidate}: {ex.Message}");
                continue;
            }

            _listener = listener;
            Port = candidate;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            LastError = null;
            _logger.Info("事件源", $"已在本机监听 {IPAddress.Loopback}:{candidate}，等待游戏推送状态");
            port = candidate;
            return true;
        }

        error = "无法绑定本机回环端口，可能是端口被占用或网络组件受限。";
        LastError = error;
        _logger.Warn("事件源", error, string.Join("；", AttemptedPorts));
        return false;
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 忽略。
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        Port = 0;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                _logger.Warn("事件源", "接受连接失败", ex.Message);
                try
                {
                    await Task.Delay(150, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (!await _slots.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleClientAsync(client, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _errorCount);
                    _logger.Debug("事件源", "处理推送请求失败", ex.Message);
                }
                finally
                {
                    try
                    {
                        client.Dispose();
                    }
                    catch
                    {
                        // 忽略。
                    }

                    _slots.Release();
                }
            }, token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        client.NoDelay = true;
        await using var stream = client.GetStream();

        using var buffer = new MemoryStream(8192);
        var chunk = new byte[8192];
        var headerEnd = -1;
        var contentLength = 0;
        var totalNeeded = -1;

        while (true)
        {
            if (totalNeeded < 0 && headerEnd >= 0)
            {
                var headerText = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, headerEnd);
                contentLength = ParseContentLength(headerText);
                totalNeeded = headerEnd + contentLength;
                if (contentLength > MaxBodyBytes)
                {
                    await WriteResponseAsync(stream, 413, "Payload too large", ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _errorCount);
                    return;
                }
            }

            if (totalNeeded >= 0 && buffer.Length >= totalNeeded) break;
            if (buffer.Length > MaxBodyBytes + 16384)
            {
                await WriteResponseAsync(stream, 413, "Payload too large", ct).ConfigureAwait(false);
                Interlocked.Increment(ref _errorCount);
                return;
            }

            int read;
            read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (read <= 0) break;
            buffer.Write(chunk, 0, read);
            if (headerEnd < 0)
            {
                headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
                if (headerEnd < 0 && buffer.Length > 16384)
                {
                    await WriteResponseAsync(stream, 400, "Bad request", ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _errorCount);
                    return;
                }
            }
        }

        if (headerEnd < 0)
        {
            await WriteResponseAsync(stream, 400, "Bad request", ct).ConfigureAwait(false);
            Interlocked.Increment(ref _errorCount);
            return;
        }

        var header = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, headerEnd);
        var bodyStart = headerEnd + 4;
        var bodyLength = Math.Max(0, (int)buffer.Length - bodyStart);
        var body = bodyLength == 0 ? string.Empty : Encoding.UTF8.GetString(buffer.GetBuffer(), bodyStart, bodyLength);
        var requestLine = header.Split("\r\n", 2)[0].Trim();
        var parts = requestLine.Split(' ');
        var method = parts.Length > 0 ? parts[0] : "POST";
        var rawPath = parts.Length > 1 ? parts[1] : "/";
        var path = rawPath;
        var queryAt = path.IndexOf('?');
        if (queryAt >= 0) path = path[..queryAt];

        // 先回 2xx：CS2 只有收到成功响应才认为推送成功。
        await WriteResponseAsync(stream, 200, "{}", ct).ConfigureAwait(false);

        var gameId = ExtractGameId(path);
        Interlocked.Increment(ref _requestCount);
        lock (_gate)
        {
            _lastPayloadUtc = DateTime.UtcNow;
            _lastPayloadGameId = gameId;
        }

        try
        {
            PayloadReceived?.Invoke(new GsiRequest(
                gameId,
                path,
                method,
                body,
                client.Client.RemoteEndPoint?.ToString() ?? "本机",
                DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.Warn("事件源", "处理游戏状态推送时出错", ex.Message);
        }
    }

    private static string ExtractGameId(string path)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Length == 0) return GameCatalog.Cs2GameId;
        var segment = trimmed.Split('/')[0];
        return string.IsNullOrWhiteSpace(segment) ? GameCatalog.Cs2GameId : segment.ToLowerInvariant();
    }

    private static int ParseContentLength(string header)
    {
        foreach (var line in header.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line.AsSpan(colon + 1).Trim(), out var value) && value >= 0) return value;
        }

        return 0;
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string body, CancellationToken token)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            413 => "Payload Too Large",
            _ => "OK",
        };
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);
        await stream.WriteAsync(headerBytes.AsMemory(), token).ConfigureAwait(false);
        await stream.WriteAsync(bytes.AsMemory(), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public string DescribeEndpoint() => IsRunning ? $"http://127.0.0.1:{Port}/" : "未监听";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _slots.Dispose();
    }
}
