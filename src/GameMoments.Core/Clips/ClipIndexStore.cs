using System.Text.Json;
using GameMoments.Core.Logging;

namespace GameMoments.Core.Clips;

/// <summary>片段索引的持久化：原子写入，损坏时备份并尽量回退到 .bak。</summary>
public sealed class ClipIndexStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly AppLogger _logger;

    public ClipIndexStore(string path, AppLogger logger)
    {
        _path = path;
        _logger = logger;
    }

    public string IndexPath => _path;

    public List<ClipRecord> Load()
    {
        if (!File.Exists(_path)) return new List<ClipRecord>();

        var primary = TryRead(_path);
        if (primary is not null) return primary;

        Quarantine();
        var backupPath = _path + ".bak";
        var backup = TryRead(backupPath);
        if (backup is not null)
        {
            _logger.Warn("片段库", "片段索引损坏，已从备份恢复。", backupPath);
            return backup;
        }

        _logger.Error("片段库", "片段索引损坏且没有可用备份，将从空索引开始。", null, _path);
        return new List<ClipRecord>();
    }

    public void Save(IReadOnlyList<ClipRecord> clips)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = _path + ".tmp";
        var json = JsonSerializer.Serialize(clips, Options);
        File.WriteAllText(temp, json);

        if (File.Exists(_path))
        {
            try
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
            }
            catch
            {
                // 备份失败不影响主流程。
            }
        }

        File.Move(temp, _path, overwrite: true);
    }

    private List<ClipRecord>? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var clips = JsonSerializer.Deserialize<List<ClipRecord>>(json, Options);
            return clips?.Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id)).ToList();
        }
        catch (Exception ex)
        {
            _logger.Warn("片段库", $"读取片段索引失败：{ex.Message}", path);
            return null;
        }
    }

    private void Quarantine()
    {
        try
        {
            var target = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Move(_path, target, overwrite: true);
            _logger.Warn("片段库", "已把损坏的索引移到备份文件。", target);
        }
        catch (Exception ex)
        {
            _logger.Warn("片段库", $"无法隔离损坏的索引：{ex.Message}");
        }
    }
}
