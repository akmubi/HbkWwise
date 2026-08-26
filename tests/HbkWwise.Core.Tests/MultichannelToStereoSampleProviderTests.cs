using NAudio.Wave;

namespace HbkWwise.Core.Tests;

public sealed class MultichannelToStereoSampleProviderTests
{
    [Fact]
    public void Read_DownmixesTwelveChannelsWithoutChangingUnityGain()
    {
        var source = new ArraySampleProvider(12, Enumerable.Repeat(1f, 24).ToArray());
        var downmix = new MultichannelToStereoSampleProvider(source);
        var output = new float[4];

        var read = downmix.Read(output, 0, output.Length);

        Assert.Equal(4, read);
        Assert.All(output, sample => Assert.Equal(1, sample, 5));
    }

    [Fact]
    public void Read_PreservesTheRightSampleAcrossOddSizedReads()
    {
        var source = new ArraySampleProvider(3, Enumerable.Repeat(1f, 6).ToArray());
        var downmix = new MultichannelToStereoSampleProvider(source);
        var first = new float[1];
        var remaining = new float[3];

        Assert.Equal(1, downmix.Read(first, 0, first.Length));
        Assert.Equal(3, downmix.Read(remaining, 0, remaining.Length));
        Assert.Equal(1, first[0], 5);
        Assert.All(remaining, sample => Assert.Equal(1, sample, 5));
    }

    private sealed class ArraySampleProvider(int channels, float[] samples) : ISampleProvider
    {
        private int position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            var read = Math.Min(count, samples.Length - position);
            Array.Copy(samples, position, buffer, offset, read);
            position += read;
            return read;
        }
    }
}
