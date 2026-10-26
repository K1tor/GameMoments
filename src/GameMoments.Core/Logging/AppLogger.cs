using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameMoments.Core.Platform;

namespace GameMoments.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message, string? Detail = null)
{
    [JsonIgnore]
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");

    [JsonIgnore]
    public string LevelText => Level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Info => "信息",
        LogLevel.Warn => "警告",
        LogLevel.Error => "错误",
        _ => Level.ToString(),
    };
}

/// <summary>
/// 结构化日志：内存保留最近若干条供界面显示，同时按天写入 JSON Lines 文件。
/// 写入文件的文本会经过隐私替换（用户目录 → %USERPROFILE%）。
/// </summary>
public sealed class AppLogger : IDisposable
{
    public const int MaxRecentEntries = 800;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _sync = new();
    private readonly List<LogEntry> _recent = new();
    private readonly string _directory;
    private readonly int _retentionDays;
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    public event Action<LogEntry>? EntryWritten;

    public string DirectoryPath => _directory;
    public string? FilePath { get; private set; }

    public AppLogger(string directory, int retentionDays = 7)
    {
        _directory = directory;
        _retentionDays = Math.Clamp(retentionDays, 1, 90);
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch
        {
            // 无法创建日志目录时仍允许内存日志工作。
        }
        CleanupOldFiles();
    }

    public void Debug(string category, string message, string? detail = null) => Write(LogLevel.Debug, category, message, detail);
    public void Info(string category, string message, string? detail = null) => Write(LogLevel.Info, category, message, detail);
    public void Warn(string category, string message, string? detail = null) => Write(LogLevel.Warn, category, message, detail);

    public void Error(string category, string message, Exception? exception = null, string? detail = null)
    {
        var suffix = exception is null ? detail : $"{detail}{(detail is null ? string.Empty : Environment.NewLine)}{exception}";
        Write(LogLevel.Error, category, message, suffix);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync)
        {
            return _recent.ToArray();
        }
    }

    private void Write(LogLevel level, string category, string message, string? detail)
    {
        if (_disposed) return;
        var entry = new LogEntry(
            DateTimeOffset.Now,
            level,
            category,
            Privacy.Redact(message),
            string.IsNullOrEmpty(detail) ? null : Privacy.Redact(detail));

        lock (_sync)
        {
            _recent.Add(entry);
            if (_recent.Count > MaxRecentEntries)
            {
                _recent.RemoveRange(0, _recent.Count - MaxRecentEntries);
            }

            try
            {
                EnsureWriter();
                _writer!.WriteLine(JsonSerializer.Serialize(new LogFileLine
                {
                    Time = entry.Time.ToString("O"),
                    Level = entry.Level.ToString().ToLowerInvariant(),
                    Category = entry.Category,
                    Message = entry.Message,
                    Detail = entry.Detail,
                }, JsonOptions));
                _writer.Flush();
            }
            catch
            {
                // 磁盘满或文件被占用时降级为仅内存日志。
            }
        }

        try
        {
            EntryWritten?.Invoke(entry);
        }
        catch
        {
            // 订阅者异常不能影响日志系统。
        }
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _writerDate == today) return;

        _writer?.Dispose();
        FilePath = Path.Combine(_directory, $"app-{today:yyyyMMdd}.jsonl");
        _writer = new StreamWriter(
            new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 16 * 1024),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _writerDate = today;
    }

    private void CleanupOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "app-*.jsonl"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // 忽略单个文件删除失败。
                    }
                }
            }
        }
        catch
        {
            // 忽略清理失败。
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class LogFileLine
    {
        public string Time { get; set; } = string.Empty;
        public string Level { get; set; } = "info";
        public string Category { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Detail { get; set; }
    }
}
