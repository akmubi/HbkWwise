using System.Buffers.Binary;

namespace HbkWwise.Core.Tests;

public sealed class BnkTimelineEditorTests
{
    [Fact]
    public void Apply_TranslatesVisibleClipCoordinatesAndReplacementDuration()
    {
        var bank = new byte[48];
        Write(bank, 8, 100);
        Write(bank, 16, 20);
        Write(bank, 24, -30);
        Write(bank, 32, 1_000);
        var clip = new BnkTimelineClip(
            1,
            2,
            3,
            4,
            100,
            20,
            -30,
            1_000,
            120,
            1_070,
            false,
            new BnkTimelineFieldOffsets(8, 16, 24, 32));
        var validation = new BnkTimelineValidation(
            9,
            1,
            [],
            [clip],
            [],
            [],
            new BnkDurationValidation(9, [], []),
            []);

        var result = BnkTimelineEditor.Apply(
            bank,
            validation,
            [new BnkTimelineClipEdit(4, 200, 50, 500)],
            new Dictionary<uint, double> { [3] = 700 });

        Assert.Equal(150, Read(result.Data, 8));
        Assert.Equal(50, Read(result.Data, 16));
        Assert.Equal(-150, Read(result.Data, 24));
        Assert.Equal(700, Read(result.Data, 32));
        Assert.Equal(4, result.PatchCount);
    }

    [Fact]
    public void Apply_RejectsDivergentEditsForOneSharedSourceReference()
    {
        var clip = new BnkTimelineClip(
            1,
            2,
            3,
            4,
            0,
            0,
            0,
            100,
            0,
            100,
            false,
            new BnkTimelineFieldOffsets(8, 16, 24, 32));
        var validation = new BnkTimelineValidation(
            9,
            1,
            [],
            [clip],
            [],
            [],
            new BnkDurationValidation(9, [], []),
            []);

        Assert.Throws<InvalidDataException>(() => BnkTimelineEditor.Apply(
            new byte[48],
            validation,
            [new BnkTimelineClipEdit(4, 0, 0, 100), new BnkTimelineClipEdit(4, 50, 0, 100)],
            new Dictionary<uint, double>()));
    }

    [Fact]
    public void Apply_AllowsEditingOnlyTheSelectedAuthoredClips()
    {
        var bank = new byte[80];
        Write(bank, 8, 0);
        Write(bank, 16, 0);
        Write(bank, 24, 0);
        Write(bank, 32, 100);
        Write(bank, 48, 10);
        var clips = new[]
        {
            new BnkTimelineClip(1, 2, 3, 4, 0, 0, 0, 100, 0, 100, false,
                new BnkTimelineFieldOffsets(8, 16, 24, 32)),
            new BnkTimelineClip(5, 6, 7, 44, 10, 0, 0, 100, 10, 110, false,
                new BnkTimelineFieldOffsets(48, 56, 64, 72))
        };
        var validation = new BnkTimelineValidation(
            9,
            1,
            [],
            clips,
            [],
            [],
            new BnkDurationValidation(9, [], []),
            []);

        var result = BnkTimelineEditor.Apply(
            bank,
            validation,
            [new BnkTimelineClipEdit(4, 20, 0, 80)],
            new Dictionary<uint, double>());

        Assert.Equal(20, Read(result.Data, 8));
        Assert.Equal(-20, Read(result.Data, 24));
        Assert.Equal(10, Read(result.Data, 48));
        Assert.Equal(1, result.EditedClips);
    }

    [Fact]
    public void Apply_ReexpressesAnExplicitlyEditedRepeatingClip()
    {
        var bank = new byte[48];
        Write(bank, 8, -100);
        Write(bank, 16, -100);
        Write(bank, 24, 200);
        Write(bank, 32, 1_000);
        var clip = new BnkTimelineClip(
            1, 2, 3, 4, -100, -100, 200, 1_000, -200, 1_100, true,
            new BnkTimelineFieldOffsets(8, 16, 24, 32));
        var validation = new BnkTimelineValidation(
            9, 1, [], [clip], [], [], new BnkDurationValidation(9, [], []), []);

        var result = BnkTimelineEditor.Apply(
            bank,
            validation,
            [new BnkTimelineClipEdit(4, 0, 50, 1_200)],
            new Dictionary<uint, double> { [3] = 800 });

        Assert.Equal(-50, Read(result.Data, 8));
        Assert.Equal(50, Read(result.Data, 16));
        Assert.Equal(450, Read(result.Data, 24));
        Assert.Equal(800, Read(result.Data, 32));
        Assert.Equal(1, result.EditedClips);
    }

    private static void Write(byte[] data, int offset, double value) =>
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(offset, 8), value);

    private static double Read(byte[] data, int offset) =>
        BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(offset, 8));
}
