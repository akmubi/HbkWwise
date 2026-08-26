using System.Buffers.Binary;

namespace HbkWwise.Core.Tests;

public sealed class TimelineAudioRendererTests
{
    [Fact]
    public void Render_MixesAndPlacesPcmSourcesOnStereoTimeline()
    {
        var first = Temp("wav");
        var second = Temp("wav");
        var output = Temp("wav");

        try
        {
            WriteMono(first, 24_000, 2_400, 0.25f);
            WriteMono(second, 48_000, 4_800, 0.25f);

            var result = TimelineAudioRenderer.Render(
                [
                    new TimelineAudioPlacement(first, 0, 0, 100),
                    new TimelineAudioPlacement(second, 50, 0, 100)
                ],
                output);

            Assert.Equal(150, result.DurationMs);
            Assert.Equal(2, result.Placements);
            Assert.Equal(48_000, ReadInt(output, 24));
            Assert.Equal(48_000 * 2 * 2, ReadInt(output, 28));
            Assert.Equal(28_800, ChunkSize(output, "data"));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
            File.Delete(output);
        }
    }

    [Fact]
    public void Render_RepeatsAuthoredLoopToPlacementEnd()
    {
        var source = Temp("wav");
        var output = Temp("wav");

        try
        {
            WriteMono(source, 48_000, 2_400, 0.25f);

            _ = TimelineAudioRenderer.Render(
                [new TimelineAudioPlacement(source, 0, 0, 100, Repeat: true)],
                output);

            var data = File.ReadAllBytes(output);
            var chunk = FindChunk(data, "data");

            Assert.Equal(19_200, chunk.Size);
            Assert.NotEqual(
                0,
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(chunk.Offset + chunk.Size - 2, 2)));
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Fact]
    public void Render_AppliesClipFadeEnvelopes()
    {
        var source = Temp("wav");
        var output = Temp("wav");

        try
        {
            WriteMono(source, 48_000, 4_800, 0.5f);

            _ = TimelineAudioRenderer.Render(
                [new TimelineAudioPlacement(source, 0, 0, 100, FadeInMs: 50, FadeOutMs: 50)],
                output);

            var data = File.ReadAllBytes(output);
            var chunk = FindChunk(data, "data");
            var first = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(chunk.Offset, 2));
            var fadeInMiddle = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(chunk.Offset + 1_200 * 4, 2));
            var middle = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(chunk.Offset + 2_400 * 4, 2));
            var fadeOutMiddle = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(chunk.Offset + 3_600 * 4, 2));
            var last = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(chunk.Offset + chunk.Size - 4, 2));

            Assert.InRange(Math.Abs(first), 0, 1);
            Assert.InRange(Math.Abs(fadeInMiddle), 7_800, 8_500);
            Assert.True(Math.Abs(middle) > 10_000);
            Assert.InRange(Math.Abs(fadeOutMiddle), 7_800, 8_500);
            Assert.InRange(Math.Abs(last), 0, 16);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Fact]
    public void Render_AppliesTrackGain()
    {
        var source = Temp("wav");
        var output = Temp("wav");

        try
        {
            WriteMono(source, 48_000, 480, 0.5f);

            _ = TimelineAudioRenderer.Render(
                [new TimelineAudioPlacement(source, 0, 0, 10, Gain: 0.5)],
                output);

            var data = File.ReadAllBytes(output);
            var chunk = FindChunk(data, "data");
            var sample = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(chunk.Offset, 2));

            Assert.InRange(Math.Abs(sample), 8_000, 8_300);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Fact]
    public void Render_BakesLeadingSilenceIntoTheOutputWave()
    {
        var source = Temp("wav");
        var output = Temp("wav");

        try
        {
            WriteMono(source, 48_000, 4_800, 0.5f);

            var result = TimelineAudioRenderer.Render(
                [new TimelineAudioPlacement(source, 50, 0, 100)],
                output);

            var data = File.ReadAllBytes(output);
            var chunk = FindChunk(data, "data");
            var silentSample = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(chunk.Offset + 1_000 * 4, 2));
            var audioSample = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(chunk.Offset + 3_000 * 4, 2));

            Assert.Equal(150, result.DurationMs);
            Assert.Equal(0, silentSample);
            Assert.True(Math.Abs(audioSample) > 10_000);
        }
        finally
        {
            File.Delete(source);
            File.Delete(output);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidPlacements))]
    public void Render_RejectsInvalidPlacementGeometry(TimelineAudioPlacement placement)
    {
        var output = Temp("wav");

        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TimelineAudioRenderer.Render([placement], output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    public static TheoryData<TimelineAudioPlacement> InvalidPlacements => new()
    {
        new TimelineAudioPlacement("unused.wav", -1, 0, 100),
        new TimelineAudioPlacement("unused.wav", double.NaN, 0, 100),
        new TimelineAudioPlacement("unused.wav", 0, -1, 100),
        new TimelineAudioPlacement("unused.wav", 0, double.PositiveInfinity, 100),
        new TimelineAudioPlacement("unused.wav", 0, 0, 0),
        new TimelineAudioPlacement("unused.wav", 0, 0, double.NaN),
        new TimelineAudioPlacement("unused.wav", 0, 0, 100, FadeInMs: 101),
        new TimelineAudioPlacement("unused.wav", 0, 0, 100, FadeOutMs: double.PositiveInfinity),
        new TimelineAudioPlacement("unused.wav", 0, 0, 100, Gain: -0.1),
        new TimelineAudioPlacement("unused.wav", 0, 0, 100, Gain: double.NaN)
    };

    private static string Temp(string extension) =>
        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.{extension}");

    private static int ReadInt(string path, int offset)
    {
        var data = File.ReadAllBytes(path);
        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static int ChunkSize(string path, string name)
    {
        var data = File.ReadAllBytes(path);
        return FindChunk(data, name).Size;
    }

    private static (int Offset, int Size) FindChunk(byte[] data, string name)
    {
        for (var offset = 12; offset + 8 <= data.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
            if (System.Text.Encoding.ASCII.GetString(data, offset, 4) == name)
            {
                return (offset + 8, size);
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"WAV chunk {name} was not found.");
    }

    private static void WriteMono(string path, int sampleRate, int frames, float sample)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        var dataSize = frames * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(dataSize);

        for (var index = 0; index < frames; index++)
        {
            writer.Write((short)(sample * short.MaxValue));
        }
    }
}
