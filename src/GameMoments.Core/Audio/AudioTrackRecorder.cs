using GameMoments.Core.Logging;
using NAudio.Wave;

namespace GameMoments.Core.Audio;

/// <summary>
/// 单路音频采集：WASAPI 回调 → float32 双声道 → 48 kHz 重采样 → 1 秒 WAV 分块。
/// 以会话起点为时间轴，按墙钟补静音，保证与视频分段时间轴一致。
/// </summary>
public sealed class AudioTrackRecorder : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly PcmConverter _converter;
    private readonly LinearResampler _resampler;
    private readonly ChunkedWavSink _sink;
    private readonly AppLogger _logger;
    private readonly object _gate = new();
    private readonly float[] _stereo = new float[65_536];
    private readonly float[] _resampled = new float[65_536];
    private readonly short[] _pcm = new short[65_536];
    private Timer? _padTimer;
    private bool _started;
    private bool _disposed;
    private int _faultLogged;

    public AudioTrackRecorder(
        IWaveIn capture,
        string label,
        string deviceName,
        string directory,
        DateTime sessionStartUtc,
        AppLogger logger,
        float volume)
    {
        _capture = capture;
        _logger = logger;
        Label = label;
        DeviceName = deviceName;
        SessionStartUtc = sessionStartUtc;
        Volume = volume;
        CaptureFormat = capture.WaveFormat;
        _converter = new PcmConverter(capture.WaveFormat);
        _resampler = new LinearResampler(capture.WaveFormat.SampleRate);
        _sink = new ChunkedWavSink(directory, label == AudioTrackRecorderLabels.System ? "system" : "mic", sessionStartUtc);
    }

    public string Label { get; }
    public string DeviceName { get; }
    public DateTime SessionStartUtc { get; }
    public WaveFormat CaptureFormat { get; }
    public float Volume { get; set; }
    public string? Fault { get; private set; }
    public bool IsRunning { get; private set; }
    public string DirectoryPath => _sink.DirectoryPath;
    public long TotalFrames => _sink.TotalFrames;
    public DateTime SessionStart => SessionStartUtc;

    public string FormatText => $"{CaptureFormat.SampleRate} Hz / {CaptureFormat.Channels} ch / {CaptureFormat.BitsPerSample} bit";

    public IReadOnlyList<AudioChunk> Chunks => _sink.Snapshot();

    public void Start()
    {
        if (_started) return;
        _started = true;
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        try
        {
            _capture.StartRecording();
        }
        catch
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _started = false;
            throw;
        }

        IsRunning = true;
        _padTimer = new Timer(_ => PadTick(), null, 250, 200);
        _logger.Info("音频", $"{Label} 开始采集", $"{DeviceName} · {FormatText}");
    }

    public void Stop()
    {
        if (!_started && !IsRunning) return;
        _started = false;
        IsRunning = false;
        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // 设备可能已被拔出。
        }

        _padTimer?.Dispose();
        _padTimer = null;
        lock (_gate)
        {
            _sink.Complete();
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
    }

    public IDisposable Pin(DateTime fromUtc, DateTime toUtc) => _sink.Pin(fromUtc, toUtc);

    public void Prune(DateTime beforeUtc) => _sink.Prune(beforeUtc);

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || e.BytesRecorded <= 0) return;
        try
        {
            var now = DateTime.UtcNow;
            var frames = _converter.Decode(e.Buffer, e.BytesRecorded, _stereo);
            if (frames <= 0) return;

            lock (_gate)
            {
                var outFrames = _resampler.Process(_stereo.AsSpan(0, frames * 2), _resampled);
                if (outFrames <= 0) return;
                PcmConverter.ConvertToPcm16(_resampled.AsSpan(0, outFrames * 2), _pcm);

                var targetFrames = (long)((now - SessionStartUtc).TotalSeconds * WavIO.SampleRate);
                var bufferStart = targetFrames - outFrames;
                var backlog = bufferStart - _sink.TotalFrames;
                if (backlog > WavIO.SampleRate / 20)
                {
                    _sink.PadSilenceTo(bufferStart);
                }
                _sink.Append(_pcm.AsSpan(0, outFrames * 2));
            }
        }
        catch (Exception ex)
        {
            SetFault($"音频数据回调失败：{ex.Message}");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed) return;
        IsRunning = false;
        if (e.Exception is not null)
        {
            SetFault($"采集被系统中断：{e.Exception.Message}");
        }
    }

    private void PadTick()
    {
        if (_disposed || !_started) return;
        try
        {
            var target = (long)((DateTime.UtcNow - SessionStartUtc).TotalSeconds * WavIO.SampleRate) - WavIO.SampleRate / 10;
            if (_sink.TotalFrames < target)
            {
                _sink.PadSilenceTo(target);
            }
        }
        catch
        {
            // 定时补静音失败不影响主流程。
        }
    }

    private void SetFault(string message)
    {
        Fault = message;
        if (Interlocked.Exchange(ref _faultLogged, 1) == 0)
        {
            _logger.Error("音频", $"{Label} 采集异常", null, message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try
        {
            _capture.Dispose();
        }
        catch
        {
            // 忽略。
        }
        _sink.Dispose();
    }
}

public static class AudioTrackRecorderLabels
{
    public const string System = "系统声音";
    public const string Microphone = "麦克风";
}
