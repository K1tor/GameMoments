using NAudio.Wave;

namespace GameMoments.Core.Audio;

/// <summary>把 WASAPI 采集到的各种 PCM / 浮点格式统一转换为双声道 float32。</summary>
public sealed class PcmConverter
{
    private readonly int _channels;
    private readonly bool _isFloat;
    private readonly int _bitsPerSample;
    private readonly int _bytesPerSample;
    private readonly int _frameSize;

    public PcmConverter(WaveFormat format)
    {
        _channels = Math.Max(1, format.Channels);
        _bitsPerSample = Math.Max(8, format.BitsPerSample);
        _bytesPerSample = _bitsPerSample / 8;
        _frameSize = _bytesPerSample * _channels;
        _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || _bitsPerSample == 32 && format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubFormat;
    }

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    public int Channels => _channels;

    /// <summary>解码为双声道交织 float；返回帧数。</summary>
    public int Decode(byte[] buffer, int bytesRecorded, float[] stereoDestination)
    {
        if (_frameSize <= 0 || bytesRecorded < _frameSize) return 0;
        var frames = Math.Min(bytesRecorded / _frameSize, stereoDestination.Length / 2);
        var offset = 0;

        for (var frame = 0; frame < frames; frame++)
        {
            float left;
            float right;
            if (_channels == 1)
            {
                left = right = ReadSample(buffer, offset);
                offset += _bytesPerSample;
            }
            else if (_channels == 2)
            {
                left = ReadSample(buffer, offset);
                right = ReadSample(buffer, offset + _bytesPerSample);
                offset += _frameSize;
            }
            else
            {
                float leftSum = 0;
                float rightSum = 0;
                var leftCount = 0;
                var rightCount = 0;
                for (var channel = 0; channel < _channels; channel++)
                {
                    var sample = ReadSample(buffer, offset + channel * _bytesPerSample);
                    if ((channel & 1) == 0)
                    {
                        leftSum += sample;
                        leftCount++;
                    }
                    else
                    {
                        rightSum += sample;
                        rightCount++;
                    }
                }
                left = leftCount > 0 ? leftSum / leftCount : 0;
                right = rightCount > 0 ? rightSum / rightCount : left;
                offset += _frameSize;
            }

            stereoDestination[frame * 2] = left;
            stereoDestination[frame * 2 + 1] = right;
        }

        return frames;
    }

    private float ReadSample(byte[] buffer, int offset)
    {
        switch (_bitsPerSample)
        {
            case 8:
                return (buffer[offset] - 128) / 128f;
            case 16:
                return BitConverter.ToInt16(buffer, offset) / 32768f;
            case 24:
                var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                return value / 8388608f;
            case 32 when _isFloat:
                return BitConverter.ToSingle(buffer, offset);
            case 32:
                return BitConverter.ToInt32(buffer, offset) / 2147483648f;
            default:
                return 0;
        }
    }

    public static void ConvertToPcm16(ReadOnlySpan<float> stereo, Span<short> destination)
    {
        for (var i = 0; i < destination.Length && i < stereo.Length; i++)
        {
            var value = Math.Clamp(stereo[i], -1f, 1f);
            destination[i] = (short)Math.Round(value * 32767f);
        }
    }
}
