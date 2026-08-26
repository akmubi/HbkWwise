using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HbkWwise.Core.Tests;

public sealed class WaveformAnalyzerTests
{
    [Fact]
    public void Analyze_ReturnsBoundedEnvelopeAcrossTheWholeWave()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hbk-waveform-{Guid.NewGuid():N}.wav");
        try
        {
            var signal = new SignalGenerator(48_000, 1)
            {
                Frequency = 440,
                Gain = 0.5,
                Type = SignalGeneratorType.Sin
            };
            WaveFileWriter.CreateWaveFile16(path, signal.Take(TimeSpan.FromSeconds(1)));

            var envelope = WaveformAnalyzer.Analyze(path, 128);

            Assert.Equal(128, envelope.Points);
            Assert.InRange(envelope.DurationMs, 999, 1_001);
            Assert.All(envelope.Minimums, value => Assert.InRange(value, -0.51f, 0));
            Assert.All(envelope.Maximums, value => Assert.InRange(value, 0, 0.51f));
            Assert.Contains(envelope.Maximums, value => value > 0.49f);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
