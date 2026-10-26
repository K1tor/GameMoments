using System.Runtime.InteropServices;

namespace GameMoments.Core.Audio;

public sealed class AudioChunk
{
    public AudioChunk(string path, int index, DateTime startUtc)
    {
        Path = path;
        Index = index;
        StartUtc = startUtc;
    }

    public string Path { get; }
    public int Index { get; }
    public DateTime StartUtc { get; }
    public long Frames { get; internal set; }
    public bool Closed { get; internal set; }
    public DateTime EndUtc => StartUtc.AddSeconds(Frames / (double)WavIO.SampleRate);
    public double DurationSeconds => Frames / (double)WavIO.SampleRate;
}

/// <summary>
/// 把连续 PCM 写成按绝对时间对齐的 1 秒 WAV 分块（chunk 序号 * 1 秒 = 相对会话起点的时间），
/// 支持按墙钟补静音、按回放窗口清理、以及保存时的区间 pin。
/// </summary>
public sealed class ChunkedWavSink : IDisposable
{
    private static readonly short[] Silence = new short[WavIO.SampleRate * WavIO.Channels];

    private readonly string _directory;
    private readonly string _prefix;
    private readonly DateTime _sessionStartUtc;
    private readonly object _gate = new();
    private readonly List<AudioChunk> _chunks = new();
    private readonly Dictionary<string, int> _pins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AudioChunk> _all = new();

    private FileStream? _stream;
    private AudioChunk? _current;
    private int _nextIndex;
    private long _totalFrames;
    private bool _completed;
    private bool _disposed;

    public ChunkedWavSink(string directory, string prefix, DateTime sessionStartUtc)
    {
        _directory = directory;
        _prefix = prefix;
        _sessionStartUtc = sessionStartUtc;
        Directory.CreateDirectory(directory);
    }

    public string? Fault { get; private set; }

    public string DirectoryPath => _directory;

    public long TotalFrames
    {
        get
        {
            lock (_gate) return _totalFrames;
        }
    }

    public double TotalSeconds => TotalFrames / (double)WavIO.SampleRate;

    public DateTime StartUtc => _sessionStartUtc;

    public IReadOnlyList<AudioChunk> Snapshot()
    {
        lock (_gate) return _all.ToList();
    }

    public void Append(ReadOnlySpan<short> interleavedStereo)
    {
        if (interleavedStereo.Length < 2) return;
        var frames = interleavedStereo.Length / 2;
        lock (_gate)
        {
            if (_completed || _disposed || Fault is not null) return;
            try
            {
                WriteFrames(interleavedStereo[..(frames * 2)]);
            }
            catch (Exception ex)
            {
                Fault = $"写入音频分块失败：{ex.Message}";
            }
        }
    }

    public void PadSilenceTo(long targetFrames)
    {
        lock (_gate)
        {
            if (_completed || _disposed || Fault is not null) return;
            var missing = targetFrames - _totalFrames;
            if (missing <= 0) return;
            try
            {
                while (missing > 0)
                {
                    var take = (int)Math.Min(missing, Silence.Length / 2);
                    WriteFrames(Silence.AsSpan(0, take * 2));
                    missing -= take;
                }
            }
            catch (Exception ex)
            {
                Fault = $"补齐静音失败：{ex.Message}";
            }
        }
    }

    public IDisposable Pin(DateTime fromUtc, DateTime toUtc)
    {
        var paths = new List<string>();
        lock (_gate)
        {
            foreach (var chunk in _all)
            {
                if (chunk.EndUtc >= fromUtc && chunk.StartUtc <= toUtc)
                {
                    paths.Add(chunk.Path);
                }
            }
            foreach (var path in paths)
            {
                _pins[path] = _pins.TryGetValue(path, out var count) ? count + 1 : 1;
            }
        }
        return new PinToken(this, paths);
    }

    public void Prune(DateTime beforeUtc)
    {
        lock (_gate)
        {
            if (_completed || _disposed) return;
            for (var i = _chunks.Count - 1; i >= 0; i--)
            {
                var chunk = _chunks[i];
                if (!chunk.Closed || chunk == _current) continue;
                if (chunk.EndUtc >= beforeUtc) continue;
                if (_pins.TryGetValue(chunk.Path, out var pins) && pins > 0) continue;
                try
                {
                    File.Delete(chunk.Path);
                }
                catch
                {
                    // 文件可能仍被占用，下一轮再删。
                }
                _chunks.RemoveAt(i);
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            CloseCurrent();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseCurrent();
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void WriteFrames(ReadOnlySpan<short> frames)
    {
        while (frames.Length > 0)
        {
            EnsureCurrent();
            if (_current is null) return;

            var remaining = (int)(WavIO.SampleRate - _current.Frames);
            var take = Math.Min(remaining, frames.Length / 2);
            if (take <= 0)
            {
                CloseCurrent();
                continue;
            }

            var bytes = MemoryMarshal.AsBytes(frames[..(take * 2)]);
            _stream!.Write(bytes);
            _current.Frames += take;
            _totalFrames += take;
            frames = frames[(take * 2)..];

            if (_current.Frames >= WavIO.SampleRate)
            {
                CloseCurrent();
            }
        }
    }

    private void EnsureCurrent()
    {
        if (_current is not null || _stream is not null) return;

        var index = _nextIndex++;
        var path = Path.Combine(_directory, $"{_prefix}_{index:00000}.wav");
        var chunk = new AudioChunk(path, index, _sessionStartUtc.AddSeconds(index));
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _stream.Write(new byte[44]);
        _current = chunk;
        _all.Add(chunk);
        _chunks.Add(chunk);
    }

    private void CloseCurrent()
    {
        if (_current is null) return;
        try
        {
            _stream?.Flush();
            if (_stream is not null)
            {
                _stream.Seek(0, SeekOrigin.Begin);
                WavIO.WriteHeader(_stream, (int)(_current.Frames * WavIO.BytesPerFrame));
                _stream.Flush();
                _stream.Dispose();
            }
        }
        catch
        {
            // 忽略收尾失败，保留已落盘数据。
        }

        _stream = null;
        _current.Closed = true;
        _current = null;
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
        private readonly ChunkedWavSink _sink;
        private readonly IReadOnlyList<string> _paths;
        private bool _disposed;

        public PinToken(ChunkedWavSink sink, IReadOnlyList<string> paths)
        {
            _sink = sink;
            _paths = paths;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sink.Release(_paths);
        }
    }
}
