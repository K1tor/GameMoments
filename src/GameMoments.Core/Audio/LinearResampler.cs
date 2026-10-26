namespace GameMoments.Core.Audio;

/// <summary>线性插值重采样器（双声道交织），用于把设备采样率统一到 48 kHz。</summary>
public sealed class LinearResampler
{
    private readonly double _step;
    private readonly bool _passthrough;
    private float _prevLeft;
    private float _prevRight;
    private bool _hasPrevious;
    private double _position;

    public LinearResampler(int sourceSampleRate, int targetSampleRate = WavIO.SampleRate)
    {
        if (sourceSampleRate <= 0) sourceSampleRate = targetSampleRate;
        _step = sourceSampleRate / (double)targetSampleRate;
        _passthrough = Math.Abs(_step - 1.0) < 1e-9;
    }

    /// <summary>输入与输出都是双声道交织 float，返回输出的帧数。</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        var inputFrames = input.Length / 2;
        if (inputFrames == 0) return 0;

        if (_passthrough)
        {
            var count = Math.Min(inputFrames, output.Length / 2);
            input.Slice(0, count * 2).CopyTo(output);
            return count;
        }

        if (!_hasPrevious)
        {
            _prevLeft = input[0];
            _prevRight = input[1];
            _hasPrevious = true;
        }

        var maxOutputFrames = output.Length / 2;
        var written = 0;

        while (_position <= inputFrames - 1 && written < maxOutputFrames)
        {
            var index = (int)Math.Floor(_position);
            var fraction = _position - index;

            float left;
            float right;
            if (index < 0)
            {
                left = _prevLeft;
                right = _prevRight;
            }
            else
            {
                left = input[index * 2];
                right = input[index * 2 + 1];
            }

            var nextIndex = index + 1;
            float nextLeft;
            float nextRight;
            if (nextIndex < 0)
            {
                nextLeft = _prevLeft;
                nextRight = _prevRight;
            }
            else if (nextIndex >= inputFrames)
            {
                nextLeft = left;
                nextRight = right;
            }
            else
            {
                nextLeft = input[nextIndex * 2];
                nextRight = input[nextIndex * 2 + 1];
            }

            output[written * 2] = (float)(left + (nextLeft - left) * fraction);
            output[written * 2 + 1] = (float)(right + (nextRight - right) * fraction);
            written++;
            _position += _step;
        }

        _prevLeft = input[(inputFrames - 1) * 2];
        _prevRight = input[(inputFrames - 1) * 2 + 1];
        _position -= inputFrames;
        return written;
    }
}
