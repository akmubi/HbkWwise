namespace HbkWwise.Core.Tests;

public sealed class BnkTimelineEditRebaserTests
{
    [Fact]
    public void Rebase_UpdatesEveryOffsetAfterAnEarlierSameBankRewrite()
    {
        var anchor = new BnkTimelineClipAnchor(10, 20, 0, 30);
        var validation = Validation(
            new BnkTimelineClip(
                10,
                20,
                30,
                404,
                0,
                0,
                0,
                1_000,
                0,
                1_000,
                false,
                new BnkTimelineFieldOffsets(408, 416, 424, 432)),
            new BnkTimelineSegment(
                20,
                1_000,
                [10],
                [new BnkTimelineMarker(40, 500, 504)],
                520));

        var result = BnkTimelineEditRebaser.Rebase(
            validation,
            [new BnkTimelineClipEdit(4, 100, 0, 900, anchor)],
            [new BnkTrackPlaylistEdit(10,
            [
                new BnkTrackPlaylistItemEdit(
                    4,
                    30,
                    0,
                    0,
                    100,
                    0,
                    900,
                    1_000,
                    OriginalAnchor: anchor)
            ])],
            [new BnkTimelineMarkerEdit(8, 600, new BnkTimelineMarkerAnchor(20, 40))],
            [new BnkTimelineSegmentDurationEdit(16, 1_200, 20)]);

        Assert.Equal(404, Assert.Single(result.TimelineEdits!).SourceIdOffset);
        Assert.Equal(404, Assert.Single(Assert.Single(result.PlaylistEdits!).Items).OriginalSourceIdOffset);
        Assert.Equal(504, Assert.Single(result.MarkerEdits!).PositionOffset);
        Assert.Equal(520, Assert.Single(result.SegmentDurationEdits!).DurationOffset);
    }

    [Fact]
    public void Rebase_RejectsAnAnchorThatIsNoLongerInTheScope()
    {
        var edit = new BnkTimelineClipEdit(
            4,
            0,
            0,
            100,
            new BnkTimelineClipAnchor(10, 20, 0, 30));

        var exception = Assert.Throws<InvalidDataException>(() => BnkTimelineEditRebaser.Rebase(
            Validation(),
            [edit],
            null,
            null,
            null));

        Assert.Contains("no longer has one authored clip", exception.Message);
    }

    private static BnkTimelineValidation Validation(
        BnkTimelineClip? clip = null,
        BnkTimelineSegment? segment = null) => new(
        1,
        1,
        segment is null ? [] : [segment],
        clip is null ? [] : [clip],
        [],
        [],
        new BnkDurationValidation(1, [], []),
        []);
}
