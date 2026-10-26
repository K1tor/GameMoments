using GameMoments.Core.Logging;

namespace GameMoments.Core.Clips;

/// <summary>内存中的片段集合，负责索引的增删改查与落盘。文件系统操作（删除/重命名）由调用方决定。</summary>
public sealed class ClipLibrary
{
    private readonly ClipIndexStore _store;
    private readonly AppLogger _logger;
    private readonly List<ClipRecord> _clips = new();
    private readonly object _gate = new();

    public ClipLibrary(ClipIndexStore store, AppLogger logger)
    {
        _store = store;
        _logger = logger;
        Reload();
    }

    public event Action? Changed;

    public string IndexPath => _store.IndexPath;

    /// <summary>按创建时间倒序返回快照。</summary>
    public IReadOnlyList<ClipRecord> All
    {
        get
        {
            lock (_gate) return _clips.OrderByDescending(c => c.CreatedAt).ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _clips.Count;
        }
    }

    public ClipRecord? Find(string id)
    {
        lock (_gate) return _clips.FirstOrDefault(c => c.Id == id);
    }

    public void Reload()
    {
        var loaded = _store.Load();
        lock (_gate)
        {
            _clips.Clear();
            _clips.AddRange(loaded);
        }
        Changed?.Invoke();
    }

    public void Add(ClipRecord clip)
    {
        lock (_gate)
        {
            _clips.RemoveAll(c => c.Id == clip.Id);
            _clips.Add(clip);
            PersistLocked();
        }
        Changed?.Invoke();
    }

    public void Update(ClipRecord clip)
    {
        lock (_gate)
        {
            var index = _clips.FindIndex(c => c.Id == clip.Id);
            if (index >= 0) _clips[index] = clip;
            else _clips.Add(clip);
            PersistLocked();
        }
        Changed?.Invoke();
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _clips.RemoveAll(c => c.Id == id) > 0;
            if (removed) PersistLocked();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>重命名磁盘文件并更新索引；失败时返回 false 且不改变索引。</summary>
    public bool Rename(string id, string? requestedName, out string? error)
    {
        error = null;
        var clip = Find(id);
        if (clip is null)
        {
            error = "片段不存在。";
            return false;
        }

        var baseName = Configuration.AppSettings.SanitizeFileName((requestedName ?? string.Empty).Trim());
        if (baseName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }
        if (baseName.Length == 0)
        {
            error = "名称不能为空或只包含非法字符。";
            return false;
        }
        if (baseName.Length > 80) baseName = baseName[..80];

        var directory = Path.GetDirectoryName(clip.FilePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            error = "片段所在目录不存在。";
            return false;
        }

        var targetPath = Path.Combine(directory, baseName + ".mp4");
        if (string.Equals(targetPath, clip.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            error = "新名称与当前名称相同。";
            return false;
        }
        if (File.Exists(targetPath))
        {
            error = "同目录下已存在同名片段。";
            return false;
        }

        try
        {
            File.Move(clip.FilePath, targetPath);
        }
        catch (Exception ex)
        {
            error = $"重命名失败：{ex.Message}";
            return false;
        }

        var updated = new ClipRecord
        {
            Id = clip.Id,
            FilePath = targetPath,
            FileName = Path.GetFileName(targetPath),
            CreatedAt = clip.CreatedAt,
            SavedAtUtc = clip.SavedAtUtc,
            DurationSeconds = clip.DurationSeconds,
            Width = clip.Width,
            Height = clip.Height,
            Fps = clip.Fps,
            FileSize = new FileInfo(targetPath).Length,
            HasAudio = clip.HasAudio,
            AudioSummary = clip.AudioSummary,
            ThumbnailPath = clip.ThumbnailPath,
            ManualRecording = clip.ManualRecording,
        };

        lock (_gate)
        {
            var index = _clips.FindIndex(c => c.Id == id);
            if (index >= 0) _clips[index] = updated;
            PersistLocked();
        }
        Changed?.Invoke();
        _logger.Info("片段库", $"已重命名片段：{clip.FileName} → {updated.FileName}");
        return true;
    }

    /// <summary>移除索引里文件已不存在的记录，返回被移除的数量。</summary>
    public int PruneMissing()
    {
        int removed;
        lock (_gate)
        {
            removed = _clips.RemoveAll(c => !File.Exists(c.FilePath));
            if (removed > 0) PersistLocked();
        }
        if (removed > 0)
        {
            _logger.Warn("片段库", $"清理了 {removed} 条文件已丢失的索引记录。");
            Changed?.Invoke();
        }
        return removed;
    }

    public IReadOnlyList<ClipRecord> MissingFiles()
    {
        lock (_gate) return _clips.Where(c => !File.Exists(c.FilePath)).ToList();
    }

    private void PersistLocked()
    {
        try
        {
            _store.Save(_clips);
        }
        catch (Exception ex)
        {
            _logger.Error("片段库", $"保存片段索引失败：{ex.Message}", ex);
        }
    }
}
