using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HbkWwise.Core;

public sealed record TimelineAudioPlacement(
    string WavPath,
    double StartMs,
    double SourceOffsetMs,
    double DurationMs,
    bool Repeat = false,
    double FadeInMs = 0,
    double FadeOutMs = 0,
    double Gain = 1);

public sealed record TimelineAudioRenderResult(string OutputPath, double DurationMs, int Placements);

public static class TimelineAudioRenderer
{
    private const int OutputRate = 48_000;

    public static TimelineAudioRenderResult Render(
        IReadOnlyCollection<TimelineAudioPlacement> placements,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placements);
        if (placements.Count == 0)
        {
            throw new ArgumentException("At least one audio placement is required.", nameof(placements));
        }

        foreach (var placement in placements)
        {
            ValidatePlacement(placement);
        }

        var durationMs = placements.Max(item => item.StartMs + item.DurationMs);
        var readers = new List<WaveStream>();
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(OutputRate, 2))
        {
            ReadFully = true
        };

        try
        {
            foreach (var placement in placements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var reader = new WaveFileReader(Path.GetFullPath(placement.WavPath));
                readers.Add(reader);

                WaveStream waveSource = placement.Repeat ? new LoopingWaveStream(reader) : reader;
                ISampleProvider source = waveSource.ToSampleProvider();
                source = source.WaveFormat.Channels switch
                {
                    1 => new MonoToStereoSampleProvider(source),
                    2 => source,
                    _ => new MultichannelToStereoSampleProvider(source)
                };

                if (source.WaveFormat.SampleRate != OutputRate)
                {
                    source = new WdlResamplingSampleProvider(source, OutputRate);
                }

                var window = new OffsetSampleProvider(source)
                {
                    SkipOver = TimeSpan.FromMilliseconds(placement.SourceOffsetMs),
                    Take = TimeSpan.FromMilliseconds(placement.DurationMs)
                };
                ISampleProvider shaped = placement.FadeInMs <= 0 && placement.FadeOutMs <= 0
                    ? window
                    : new FadeEnvelopeSampleProvider(
                        window,
                        placement.DurationMs,
                        placement.FadeInMs,
                        placement.FadeOutMs);

                shaped = new VolumeSampleProvider(shaped) { Volume = (float)placement.Gain };
                mixer.AddMixerInput(new OffsetSampleProvider(shaped)
                {
                    DelayBy = TimeSpan.FromMilliseconds(placement.StartMs)
                });
            }

            var output = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            var temporary = $"{output}.{Guid.NewGuid():N}.tmp.wav";
            try
            {
                WaveFileWriter.CreateWaveFile16(
                    temporary,
                    mixer.Take(TimeSpan.FromMilliseconds(durationMs)));
                File.Move(temporary, output, true);
            }
            finally
            {
                File.Delete(temporary);
            }

            return new TimelineAudioRenderResult(output, durationMs, placements.Count);
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static void ValidatePlacement(TimelineAudioPlacement placement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placement.WavPath);

        if (!double.IsFinite(placement.StartMs) || placement.StartMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement.StartMs),
                "Placement start must be a finite, non-negative value.");
        }

        if (!double.IsFinite(placement.SourceOffsetMs) || placement.SourceOffsetMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement.SourceOffsetMs),
                "Placement source offset must be a finite, non-negative value.");
        }

        if (!double.IsFinite(placement.DurationMs) || placement.DurationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement.DurationMs),
                "Placement duration must be a finite, positive value.");
        }

        if (!double.IsFinite(placement.FadeInMs)
            || placement.FadeInMs < 0
            || placement.FadeInMs > placement.DurationMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement.FadeInMs),
                "Placement fade-in must be finite and within the placement duration.");
        }

        if (!double.IsFinite(placement.FadeOutMs)
            || placement.FadeOutMs < 0
            || placement.FadeOutMs > placement.DurationMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement.FadeOutMs),
                "Placement fade-out must be finite and within the placement duration.");
        }

        if (!double.IsFinite(placement.Gain) || placement.Gain is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(placement.Gain),
                "Placement gain must be between 0 and 2.");
        }
    }

    private sealed class FadeEnvelopeSampleProvider(
        ISampleProvider source,
        double durationMs,
        double fadeInMs,
        double fadeOutMs) : ISampleProvider
    {
        private long samplePosition;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            var channels = WaveFormat.Channels;

            for (var index = 0; index < read; index += channels)
            {
                var frame = (samplePosition + index) / channels;
                var timeMs = frame * 1000d / WaveFormat.SampleRate;
                var fadeIn = fadeInMs <= 0 ? 1 : Math.Clamp(timeMs / fadeInMs, 0, 1);
                var fadeOut = fadeOutMs <= 0 ? 1 : Math.Clamp((durationMs - timeMs) / fadeOutMs, 0, 1);
                var gain = (float)Math.Min(fadeIn, fadeOut);

                for (var channel = 0; channel < channels && index + channel < read; channel++)
                {
                    buffer[offset + index + channel] *= gain;
                }
            }

            samplePosition += read;
            return read;
        }
    }

    private sealed class LoopingWaveStream(WaveStream source) : WaveStream
    {
        public override WaveFormat WaveFormat => source.WaveFormat;
        public override long Length => long.MaxValue;

        public override long Position
        {
            get => source.Position;
            set => source.Position = value % source.Length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = source.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    source.Position = 0;
                    read = source.Read(buffer, offset + total, count - total);
                    if (read == 0)
                    {
                        break;
                    }
                }

                total += read;
            }

            return total;
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
