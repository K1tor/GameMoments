using System.Text;

namespace GameMoments.Core.Audio;

public sealed record WavData(short[] Samples, int SampleRate, int Channels)
{
    public int Frames => Samples.Length / Math.Max(1, Channels);
    public double DurationSeconds => Frames / (double)Math.Max(1, SampleRate);
}

/// <summary>固定输出 48 kHz / 双声道 / 16-bit PCM 的 WAV 读写与裁剪。</summary>
public static class WavIO
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BytesPerFrame = 4;

    public static void WriteFile(string path, ReadOnlySpan<short> interleavedSamples)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteHeader(stream, interleavedSamples.Length * 2);
        stream.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleavedSamples));
    }

    public static void WriteHeader(Stream stream, int dataBytes)
    {
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * 2);
        writer.Write((short)(Channels * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Flush();
    }

    public static WavData Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        var riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (riff != "RIFF") throw new InvalidDataException($"不是有效的 WAV 文件：{path}");
        reader.ReadInt32();
        var wave = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (wave != "WAVE") throw new InvalidDataException($"不是有效的 WAV 文件：{path}");

        var channels = 0;
        var sampleRate = 0;
        var bits = 0;
        short[]? samples = null;

        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadInt32();
            if (size < 0) break;

            if (id == "fmt ")
            {
                reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
                if (size > 16) reader.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                var available = (int)Math.Max(0, stream.Length - stream.Position);
                var count = Math.Min(size, available);
                var bytes = reader.ReadBytes(count);
                samples = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
                break;
            }
            else
            {
                stream.Position += size + (size & 1);
            }
        }

        if (samples is null) throw new InvalidDataException($"WAV 文件缺少 data 块：{path}");
        if (channels <= 0 || sampleRate <= 0) throw new InvalidDataException($"WAV 文件缺少有效的 fmt 块：{path}");
        if (bits != 16) throw new NotSupportedException($"仅支持 16-bit PCM WAV：{path}");

        return new WavData(samples, sampleRate, channels);
    }

    /// <summary>从按序号排列的 1 秒分块中裁剪出指定起点与长度的 WAV；不足部分补静音。</summary>
    public static void Extract(IReadOnlyList<string> chunkPaths, string outputPath, double startOffsetSeconds, double durationSeconds)
    {
        var totalFrames = (long)Math.Round(Math.Max(0, durationSeconds) * SampleRate);
        var startFrame = (long)Math.Round(Math.Max(0, startOffsetSeconds) * SampleRate);

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        output.Write(new byte[44]);
        long written = 0;
        long chunkStartFrame = 0;

        foreach (var path in chunkPaths)
        {
            WavData data;
            try
            {
                data = Read(path);
            }
            catch
            {
                continue;
            }

            var frames = data.Samples.Length / 2;
            var chunkEnd = chunkStartFrame + frames;
            var copyStart = Math.Max(startFrame, chunkStartFrame);
            var copyEnd = Math.Min(startFrame + totalFrames, chunkEnd);
            if (copyEnd > copyStart)
            {
                var offsetSamples = (int)((copyStart - chunkStartFrame) * 2);
                var countSamples = (int)((copyEnd - copyStart) * 2);
                output.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.Samples.AsSpan(offsetSamples, countSamples)));
                written += countSamples / 2;
            }
            chunkStartFrame = chunkEnd;
        }

        var missing = totalFrames - written;
        if (missing > 0)
        {
            var silence = new byte[SampleRate * BytesPerFrame];
            while (missing > 0)
            {
                var frames = (int)Math.Min(missing, silence.Length / BytesPerFrame);
                output.Write(silence, 0, frames * BytesPerFrame);
                missing -= frames;
            }
        }

        output.Seek(0, SeekOrigin.Begin);
        WriteHeader(output, (int)(totalFrames * BytesPerFrame));
        output.Flush();
    }
}

