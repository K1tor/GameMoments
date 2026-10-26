namespace GameMoments.Core.Capture;

public sealed class SegmentInfo
{
    public SegmentInfo(int index, string path, DateTime createdUtc, long sizeBytes, bool closed)
    {
        Index = index;
        Path = path;
        CreatedUtc = createdUtc;
        SizeBytes = sizeBytes;
        IsClosed = closed;
    }

    public int Index { get; }
    public string Path { get; }
    public DateTime CreatedUtc { get; }
    public long SizeBytes { get; internal set; }
    public bool IsClosed { get; internal set; }
    public DateTime EstimatedEndUtc => CreatedUtc.AddSeconds(1);
}

/// <summary>
/// 有界的 1 秒分段环形缓存。以文件创建时间作为绝对时间锚点，负责：
/// 发现新分段、标记已关闭分段、按回放窗口/总量上限清理未 pin 的旧分段。
/// </summary>
public sealed class SegmentRing
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly List<SegmentInfo> _segments = new();
    private readonly Dictionary<string, int> _pins = new(StringComparer.OrdinalIgnoreCase);

    public SegmentRing(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => _directory;
    public int ReplaySeconds { get; set; } = 30;
    public bool RetainAll { get; set; }
    public long MaxBytes { get; set; } = 40L * 1024 * 1024 * 1024;

    public DateTime? OldestUtc
    {
        get
        {
            lock (_gate) return _segments.Count == 0 ? null : _segments[0].CreatedUtc;
        }
    }

    public DateTime? NewestUtc
    {
        get
        {
            lock (_gate) return _segments.Count == 0 ? null : _segments[^1].CreatedUtc;
        }
    }

    public long TotalBytes
    {
        get
        {
            lock (_gate) return _segments.Sum(s => s.SizeBytes);
        }
    }

    public int SegmentCount
    {
        get
        {
            lock (_gate) return _segments.Count;
        }
    }

    public double CachedSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_segments.Count == 0) return 0;
                return Math.Max(0, (_segments[^1].CreatedUtc - _segments[0].CreatedUtc).TotalSeconds) + 1;
            }
        }
    }

    public IReadOnlyList<SegmentInfo> Snapshot()
    {
        lock (_gate) return _segments.ToList();
    }

    /// <summary>扫描目录，返回缓存是否发生变化。</summary>
    public bool Scan()
    {
        lock (_gate)
        {
            var files = new List<(int Index, string Path)>();
            if (Directory.Exists(_directory))
            {
                foreach (var path in Directory.EnumerateFiles(_directory, "seg_*.mp4"))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var digits = name.Length > 4 ? name[4..] : string.Empty;
                    if (int.TryParse(digits, out var index))
                    {
                        files.Add((index, path));
                    }
                }
            }

            if (files.Count == 0) return false;
            files.Sort((a, b) => a.Index.CompareTo(b.Index));
            var maxIndex = files[^1].Index;
            var changed = false;

            foreach (var (index, path) in files)
            {
                long size;
                DateTime created;
                try
                {
                    var info = new FileInfo(path);
                    size = info.Length;
                    created = info.CreationTimeUtc;
                }
                catch
                {
                    continue;
                }

                var existing = _segments.FirstOrDefault(s => s.Index == index);
                var closed = index < maxIndex;
                if (existing is null)
                {
                    _segments.Add(new SegmentInfo(index, path, created, size, closed));
                    changed = true;
                }
                else
                {
                    existing.SizeBytes = size;
                    if (closed && !existing.IsClosed)
                    {
                        existing.IsClosed = true;
                        changed = true;
                    }
                }
            }

            // 移除被外部删除的分段。
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                if (!File.Exists(_segments[i].Path))
                {
                    _segments.RemoveAt(i);
                    changed = true;
                }
            }

            _segments.Sort((a, b) => a.Index.CompareTo(b.Index));
            if (PruneInternal()) changed = true;
            return changed;
        }
    }

    public void MarkAllClosed()
    {
        lock (_gate)
        {
            foreach (var segment in _segments) segment.IsClosed = true;
        }
    }

    /// <summary>选出与 [startUtc, endUtc] 有交集且已写完的分段。</summary>
    public IReadOnlyList<SegmentInfo> Select(DateTime startUtc, DateTime endUtc)
    {
        lock (_gate)
        {
            return _segments
                .Where(s => s.IsClosed && s.CreatedUtc <= endUtc && s.EstimatedEndUtc >= startUtc)
                .OrderBy(s => s.Index)
                .ToList();
        }
    }

    public bool HasClosedSegmentAfter(DateTime utc)
    {
        lock (_gate) return _segments.Any(s => s.IsClosed && s.CreatedUtc > utc);
    }

    public bool HasSegmentAfter(DateTime utc)
    {
        lock (_gate) return _segments.Any(s => s.CreatedUtc > utc);
    }

    public IDisposable Pin(IEnumerable<SegmentInfo> segments)
    {
        var paths = segments.Select(s => s.Path).ToList();
        lock (_gate)
        {
            foreach (var path in paths)
            {
                _pins[path] = _pins.TryGetValue(path, out var count) ? count + 1 : 1;
            }
        }
        return new PinToken(this, paths);
    }

    private bool PruneInternal()
    {
        if (_segments.Count == 0) return false;
        var newest = _segments[^1].CreatedUtc;
        var cutoff = RetainAll ? DateTime.MinValue : newest.AddSeconds(-(ReplaySeconds + 2));
        var removed = false;

        for (var i = 0; i < _segments.Count;)
        {
            var segment = _segments[i];
            var isNewest = i == _segments.Count - 1;
            var overCutoff = segment.CreatedUtc < cutoff;
            var overSize = TotalBytes > MaxBytes;
            if (isNewest || !segment.IsClosed || (!overCutoff && !overSize))
            {
                i++;
                continue;
            }
            if (_pins.TryGetValue(segment.Path, out var pins) && pins > 0)
            {
                i++;
                continue;
            }

            try
            {
                File.Delete(segment.Path);
            }
            catch
            {
                i++;
                continue;
            }
            _segments.RemoveAt(i);
            removed = true;
        }

        return removed;
    }

    private void Release(IReadOnlyList<string> paths)
    {
        lock (_gate)
        {
            foreach (var path in paths)
            {
                if (!_pins.TryGetValue(path, out var count)) continue;
                if (count <= 1) _pins.Remove(path);
                else _pins[path] = count - 1;
            }
        }
    }

    private sealed class PinToken : IDisposable
    {
        private readonly SegmentRing _ring;
        private readonly IReadOnlyList<string> _paths;
        private bool _disposed;

        public PinToken(SegmentRing ring, IReadOnlyList<string> paths)
        {
            _ring = ring;
            _paths = paths;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ring.Release(_paths);
        }
    }
}
