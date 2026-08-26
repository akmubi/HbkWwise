using NAudio.Wave;

namespace HbkWwise.Core;

public sealed record WaveformEnvelope(double DurationMs, float[] Minimums, float[] Maximums)
{
    public int Points => Minimums.Length;
}

public static class WaveformAnalyzer
{
    public static WaveformEnvelope Analyze(
        string wavPath,
        int points = 32_768,
        CancellationToken cancellationToken = default)
    {
        if (points is < 16 or > 32_768)
        {
            throw new ArgumentOutOfRangeException(nameof(points));
        }

        using var reader = new WaveFileReader(Path.GetFullPath(wavPath));
        var provider = reader.ToSampleProvider();
        var channels = provider.WaveFormat.Channels;
        var totalFrames = Math.Max(1L, reader.Length / reader.WaveFormat.BlockAlign);
        var actualPoints = (int)Math.Min(points, totalFrames);
        var minimums = new float[actualPoints];
        var maximums = new float[actualPoints];
        var initialized = new bool[actualPoints];
        var buffer = new float[Math.Max(8_192, channels * 1_024)];

        long sampleIndex = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < read; index++, sampleIndex++)
            {
                var frame = sampleIndex / channels;
                var bucket = (int)Math.Min(actualPoints - 1, frame * actualPoints / totalFrames);
                var value = Math.Clamp(buffer[index], -1, 1);

                if (!initialized[bucket])
                {
                    minimums[bucket] = value;
                    maximums[bucket] = value;
                    initialized[bucket] = true;
                }
                else
                {
                    minimums[bucket] = Math.Min(minimums[bucket], value);
                    maximums[bucket] = Math.Max(maximums[bucket], value);
                }
            }
        }

        return new WaveformEnvelope(reader.TotalTime.TotalMilliseconds, minimums, maximums);
    }
}
