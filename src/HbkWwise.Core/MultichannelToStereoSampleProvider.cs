using NAudio.Wave;

namespace HbkWwise.Core;

public sealed class MultichannelToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly int channels;
    private readonly float leftScale;
    private readonly float rightScale;
    private float[] sourceBuffer = [];
    private float pendingRight;
    private bool hasPendingRight;

    public MultichannelToStereoSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.Channels < 3)
        {
            throw new ArgumentException("Multichannel stereo downmixing requires at least three channels.", nameof(source));
        }

        this.source = source;
        channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

        var leftWeight = 1d;
        var rightWeight = 1d;
        if (channels > 2)
        {
            leftWeight += Math.Sqrt(0.5);
            rightWeight += Math.Sqrt(0.5);
        }

        if (channels > 3)
        {
            leftWeight += 0.5;
            rightWeight += 0.5;
        }

        for (var channel = 4; channel < channels; channel++)
        {
            if ((channel & 1) == 0)
            {
                leftWeight++;
            }
            else
            {
                rightWeight++;
            }
        }

        leftScale = (float)(1 / leftWeight);
        rightScale = (float)(1 / rightWeight);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var written = 0;
        if (hasPendingRight && count > 0)
        {
            buffer[offset] = pendingRight;
            hasPendingRight = false;
            written++;
        }

        var framesRequested = (count - written + 1) / 2;
        if (framesRequested <= 0)
        {
            return written;
        }

        var samplesRequested = framesRequested * channels;
        if (sourceBuffer.Length < samplesRequested)
        {
            sourceBuffer = new float[samplesRequested];
        }

        var sourceRead = source.Read(sourceBuffer, 0, samplesRequested);
        var framesRead = sourceRead / channels;
        for (var frame = 0; frame < framesRead; frame++)
        {
            var input = frame * channels;
            var left = sourceBuffer[input];
            var right = sourceBuffer[input + 1];
            if (channels > 2)
            {
                var center = sourceBuffer[input + 2] * MathF.Sqrt(0.5f);
                left += center;
                right += center;
            }

            if (channels > 3)
            {
                var lowFrequency = sourceBuffer[input + 3] * 0.5f;
                left += lowFrequency;
                right += lowFrequency;
            }

            for (var channel = 4; channel < channels; channel++)
            {
                if ((channel & 1) == 0)
                {
                    left += sourceBuffer[input + channel];
                }
                else
                {
                    right += sourceBuffer[input + channel];
                }
            }

            buffer[offset + written++] = left * leftScale;
            var mixedRight = right * rightScale;
            if (written < count)
            {
                buffer[offset + written++] = mixedRight;
            }
            else
            {
                pendingRight = mixedRight;
                hasPendingRight = true;
                break;
            }
        }

        return written;
    }
}
