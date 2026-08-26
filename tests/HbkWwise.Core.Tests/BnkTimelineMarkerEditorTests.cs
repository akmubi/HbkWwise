using System.Buffers.Binary;

namespace HbkWwise.Core.Tests;

public sealed class BnkTimelineMarkerEditorTests
{
    [Fact]
    public void Apply_PatchesCuePositionsAndDeduplicatesLinkedOffsets()
    {
        var bank = new byte[32];
        BinaryPrimitives.WriteDoubleLittleEndian(bank.AsSpan(8, 8), 1_000);

        var result = BnkTimelineMarkerEditor.Apply(bank,
        [
            new BnkTimelineMarkerEdit(8, 1_750),
            new BnkTimelineMarkerEdit(8, 1_750)
        ]);

        Assert.Equal(1, result.PatchCount);
        Assert.Equal(1_750, BinaryPrimitives.ReadDoubleLittleEndian(result.Data.AsSpan(8, 8)));
        Assert.Equal(1_000, BinaryPrimitives.ReadDoubleLittleEndian(bank.AsSpan(8, 8)));
    }

    [Fact]
    public void Apply_RejectsConflictingLinkedCuePositions()
    {
        var exception = Assert.Throws<InvalidDataException>(() => BnkTimelineMarkerEditor.Apply(
            new byte[32],
            [new BnkTimelineMarkerEdit(8, 1_000), new BnkTimelineMarkerEdit(8, 2_000)]));

        Assert.Contains("Conflicting cue edits", exception.Message);
    }

    [Fact]
    public void Apply_CanExtendSegmentForAnOverfitExitCue()
    {
        var bank = new byte[32];
        BinaryPrimitives.WriteDoubleLittleEndian(bank.AsSpan(16, 8), 4_000);

        var result = BnkTimelineMarkerEditor.Apply(
            bank,
            [],
            [new BnkTimelineSegmentDurationEdit(16, 6_000)]);

        Assert.Equal(1, result.PatchCount);
        Assert.Equal(6_000, BinaryPrimitives.ReadDoubleLittleEndian(result.Data.AsSpan(16, 8)));
    }
}
