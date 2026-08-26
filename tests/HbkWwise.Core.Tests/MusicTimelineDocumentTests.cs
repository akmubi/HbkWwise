namespace HbkWwise.Core.Tests;

public sealed class MusicTimelineDocumentTests
{
    [Fact]
    public void SetMarkerPosition_IsUndoableAndPreservesExportOffsets()
    {
        var marker = new MusicTimelineMarker(7, "Cue", 1_000, 20, [40, 48]);
        var document = new MusicTimelineDocument(120, createDefaultTrack: false);

        document.Reset(120, 4_000, [], [marker]);

        document.SetMarkerPosition(marker, 1_500);

        var moved = Assert.Single(document.Markers);

        Assert.Equal(1_500, moved.PositionMs);
        Assert.Equal([40, 48], moved.PositionOffsets!);

        document.Undo();

        Assert.Equal(1_000, Assert.Single(document.Markers).PositionMs);
    }

    [Fact]
    public void MovingExitCueResizesOnlyItsSegment()
    {
        var first = new MusicTimelineTrack(Guid.NewGuid(), "First", [], SegmentObjectId: 10, LengthMs: 4_000);
        var second = new MusicTimelineTrack(Guid.NewGuid(), "Second", [], SegmentObjectId: 20, LengthMs: 5_000);
        var exit = new MusicTimelineMarker(1539036744, "Exit", 4_000, 10, [64]);
        var document = new MusicTimelineDocument(120, createDefaultTrack: false);

        document.Reset(120, 5_000, [first, second], [exit]);

        document.SetMarkerPosition(exit, 6_000);

        Assert.Equal(6_000, document.Tracks.Single(track => track.SegmentObjectId == 10).LengthMs);
        Assert.Equal(5_000, document.Tracks.Single(track => track.SegmentObjectId == 20).LengthMs);
        Assert.Equal(6_000, document.TimelineLengthMs);
    }

    [Fact]
    public void Snap_UsesTempoAndBeatSubdivision()
    {
        var document = new MusicTimelineDocument(120, 4, 2);

        Assert.Equal(250, document.GridMilliseconds);
        Assert.Equal(750, document.Snap(630));
    }

    [Fact]
    public void SnapNear_UsesGridClipEdgesAndMarkersOnlyWithinTolerance()
    {
        var document = new MusicTimelineDocument(120);
        var track = document.Tracks[0].Id;

        document.AddClip(track, "Clip", 1_000, 500);
        document.Reset(
            120,
            2_000,
            document.Tracks,
            [new MusicTimelineMarker(1, "Cue", 1_750)]);

        Assert.Equal(1_000, document.SnapNear(1_018, 20));
        Assert.Equal(1_500, document.SnapNear(1_482, 20));
        Assert.Equal(1_750, document.SnapNear(1_768, 20));
        Assert.Equal(1_370, document.SnapNear(1_370, 20));
    }

    [Fact]
    public void SnapNear_UsesTheDisplayedGridInsteadOfHiddenBeatLines()
    {
        var document = new MusicTimelineDocument(120);

        Assert.Equal(1_030, document.SnapNear(1_030, 40, gridStepMs: 2_000));
        Assert.Equal(2_000, document.SnapNear(1_980, 40, gridStepMs: 2_000));
    }

    [Fact]
    public void MoveClip_CanChangeTrackAndUndo()
    {
        var document = new MusicTimelineDocument(120);
        var sourceTrack = document.Tracks[0].Id;
        var targetTrack = document.AddTrack();
        var clipId = document.AddClip(sourceTrack, "Verse", 0, 2_000, 42);

        document.MoveClip(clipId, targetTrack, 740);

        var moved = document.FindClip(clipId);

        Assert.Equal(targetTrack, moved.Track.Id);
        Assert.Equal(500, moved.Clip.StartMs);

        document.Undo();

        Assert.Equal(sourceTrack, document.FindClip(clipId).Track.Id);

        document.Redo();

        Assert.Equal(targetTrack, document.FindClip(clipId).Track.Id);
    }

    [Fact]
    public void SplitClip_PreservesSourcePosition()
    {
        var document = new MusicTimelineDocument(120);
        var clipId = document.AddClip(document.Tracks[0].Id, "Chorus", 0, 4_000);

        var rightId = document.SplitClip(clipId, 1_900);

        Assert.Equal(2_000, document.FindClip(clipId).Clip.DurationMs);

        var right = document.FindClip(rightId).Clip;

        Assert.Equal(2_000, right.StartMs);
        Assert.Equal(2_000, right.SourceOffsetMs);
        Assert.Equal(2_000, right.DurationMs);
    }

    [Fact]
    public void Fades_AreUndoableAndSplitAcrossTheNewBoundary()
    {
        var document = new MusicTimelineDocument(120);
        var clipId = document.AddClip(document.Tracks[0].Id, "Crossfade", 0, 4_000);

        document.SetClipFades(clipId, 500, 750);

        var rightId = document.SplitClip(clipId, 2_000);

        var left = document.FindClip(clipId).Clip;
        var right = document.FindClip(rightId).Clip;

        Assert.Equal(500, left.FadeInMs);
        Assert.Equal(0, left.FadeOutMs);
        Assert.Equal(0, right.FadeInMs);
        Assert.Equal(750, right.FadeOutMs);

        document.Undo();

        Assert.Equal(750, document.FindClip(clipId).Clip.FadeOutMs);
    }

    [Fact]
    public void MakeFadeFromInteriorSelection_TrimsTheNearestEnd()
    {
        var document = new MusicTimelineDocument(snapEnabled: false);
        var clipId = document.AddClip(document.Tracks[0].Id, "Song", 0, 10_000);

        var result = document.MakeFadeFromSelection(clipId, 7_000, 8_000);

        var clip = document.FindClip(clipId).Clip;
        Assert.Equal(MusicTimelineFadeKind.FadeOut, result.Kind);
        Assert.True(result.TrimmedClip);
        Assert.Equal(8_000, clip.DurationMs);
        Assert.Equal(1_000, clip.FadeOutMs);

        document.Undo();
        Assert.Equal(10_000, document.FindClip(clipId).Clip.DurationMs);
    }

    [Fact]
    public void MakeFadeNearClipStart_TrimsTheHeadAndAdvancesSourceOffset()
    {
        var document = new MusicTimelineDocument(snapEnabled: false);
        var clipId = document.AddClip(document.Tracks[0].Id, "Song", 0, 10_000);
        document.SetClipArrangement(clipId, 0, 500, 10_000, false, 0, 0);

        var result = document.MakeFadeFromSelection(clipId, 1_000, 2_500);

        var clip = document.FindClip(clipId).Clip;
        Assert.Equal(MusicTimelineFadeKind.FadeIn, result.Kind);
        Assert.True(result.TrimmedClip);
        Assert.Equal(1_000, clip.StartMs);
        Assert.Equal(1_500, clip.SourceOffsetMs);
        Assert.Equal(9_000, clip.DurationMs);
        Assert.Equal(1_500, clip.FadeInMs);
    }

    [Fact]
    public void MakeFadeFromSelection_ExplicitKindOverridesTheNearestEnd()
    {
        var document = new MusicTimelineDocument(snapEnabled: false);
        var clipId = document.AddClip(document.Tracks[0].Id, "Song", 0, 10_000);

        var fadeIn = document.MakeFadeFromSelection(
            clipId,
            7_000,
            8_000,
            MusicTimelineFadeKind.FadeIn);

        var clip = document.FindClip(clipId).Clip;
        Assert.Equal(MusicTimelineFadeKind.FadeIn, fadeIn.Kind);
        Assert.True(fadeIn.TrimmedClip);
        Assert.Equal(7_000, clip.StartMs);
        Assert.Equal(7_000, clip.SourceOffsetMs);
        Assert.Equal(3_000, clip.DurationMs);
        Assert.Equal(1_000, clip.FadeInMs);

        document.Undo();

        var fadeOut = document.MakeFadeFromSelection(
            clipId,
            1_000,
            2_500,
            MusicTimelineFadeKind.FadeOut);

        clip = document.FindClip(clipId).Clip;
        Assert.Equal(MusicTimelineFadeKind.FadeOut, fadeOut.Kind);
        Assert.True(fadeOut.TrimmedClip);
        Assert.Equal(2_500, clip.DurationMs);
        Assert.Equal(1_500, clip.FadeOutMs);
    }

    [Fact]
    public void RemoveClip_IsUndoable()
    {
        var document = new MusicTimelineDocument();
        var clipId = document.AddClip(document.Tracks[0].Id, "Bridge", 0, 1_000);

        document.RemoveClip(clipId);

        Assert.Throws<KeyNotFoundException>(() => document.FindClip(clipId));

        document.Undo();

        Assert.Equal("Bridge", document.FindClip(clipId).Clip.Name);
    }

    [Fact]
    public void DuplicateClip_CopiesItsSourceAndPlacesItImmediatelyAfterTheOriginal()
    {
        var document = new MusicTimelineDocument(snapEnabled: false);
        var track = document.Tracks[0].Id;
        var originalId = document.AddClip(
            track,
            "Chorus",
            250,
            900,
            mediaId: 42,
            sourcePath: "chorus.wav",
            physicalDurationMs: 1_200,
            sourceIdOffset: 12,
            replacementMediaId: 84,
            playlistIndex: 3);

        document.SetClipFades(originalId, 100, 200);

        var duplicateId = document.DuplicateClip(originalId);
        var duplicate = document.FindClip(duplicateId).Clip;

        Assert.NotEqual(originalId, duplicate.Id);
        Assert.Equal(1_150, duplicate.StartMs);
        Assert.Equal(42u, duplicate.MediaId);
        Assert.Equal("chorus.wav", duplicate.SourcePath);
        Assert.Equal(12, duplicate.SourceIdOffset);
        Assert.Equal(84u, duplicate.ReplacementMediaId);
        Assert.Equal(100, duplicate.FadeInMs);
        Assert.Equal(200, duplicate.FadeOutMs);
        Assert.Null(duplicate.PlaylistIndex);

        document.Undo();

        Assert.Throws<KeyNotFoundException>(() => document.FindClip(duplicateId));
    }

    [Fact]
    public void Snap_CanBeDisabled()
    {
        var document = new MusicTimelineDocument(120);

        document.SetSnapEnabled(false);

        Assert.Equal(630, document.Snap(630));

        document.Undo();

        Assert.Equal(500, document.Snap(630));
    }

    [Fact]
    public void RemoveTrack_RemovesItsClipsAndIsUndoable()
    {
        var document = new MusicTimelineDocument();
        var trackId = document.AddTrack("Layer");
        var clipId = document.AddClip(trackId, "Layer clip", 0, 1_000);

        document.RemoveTrack(trackId);

        Assert.DoesNotContain(document.Tracks, track => track.Id == trackId);
        Assert.Throws<KeyNotFoundException>(() => document.FindClip(clipId));

        document.Undo();

        Assert.Equal(trackId, document.FindClip(clipId).Track.Id);
    }

    [Fact]
    public void Reset_LoadsAuthoredTimelineAndClearsHistory()
    {
        var document = new MusicTimelineDocument();
        document.AddTrack();
        var track = new MusicTimelineTrack(Guid.NewGuid(), "Track 99", [], 99);
        var marker = new MusicTimelineMarker(1, "Entry", 250);

        document.Reset(136.05, 4_000, [track], [marker]);

        Assert.Equal(136.05, document.Bpm);
        Assert.Equal(4_000, document.TimelineLengthMs);
        Assert.Equal(99u, document.Tracks.Single().ObjectId);
        Assert.Equal(marker, document.Markers.Single());
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void SetBpmAndScale_PreservesEditedMusicalPositionsAndPhysicalDuration()
    {
        var clip = new MusicTimelineClip(
            Guid.NewGuid(), 1, "Edited", null, 1_000, 250, 2_000, PhysicalDurationMs: 3_000);
        var track = new MusicTimelineTrack(Guid.NewGuid(), "Song", [clip], 2, 3, 4_000);
        var document = new MusicTimelineDocument();

        document.Reset(120, 4_000, [track], [new MusicTimelineMarker(4, "Cue", 500)]);

        document.SetBpmAndScale(240);

        var scaled = document.FindClip(clip.Id).Clip;

        Assert.Equal(500, scaled.StartMs);
        Assert.Equal(125, scaled.SourceOffsetMs);
        Assert.Equal(1_000, scaled.DurationMs);
        Assert.Equal(3_000, scaled.PhysicalDurationMs);
        Assert.Equal(2_000, document.TimelineLengthMs);
        Assert.Equal(250, Assert.Single(document.Markers).PositionMs);

        document.Undo();

        Assert.Equal(120, document.Bpm);
        Assert.Equal(1_000, document.FindClip(clip.Id).Clip.StartMs);
    }

    [Fact]
    public void EmptyTimeline_AllowsAddingAndRemovingItsOnlyTrack()
    {
        var document = new MusicTimelineDocument(createDefaultTrack: false);

        Assert.Empty(document.Tracks);

        var trackId = document.AddTrack();
        document.RemoveTrack(trackId);

        Assert.Empty(document.Tracks);
    }

    [Fact]
    public void MoveClip_SnapsEitherEdgeButAllowsIntentionalOverlap()
    {
        var document = new MusicTimelineDocument(120);
        var trackId = document.Tracks[0].Id;

        document.AddClip(trackId, "First", 0, 1_000);
        var moving = document.AddClip(trackId, "Moving", 3_000, 500);

        document.MoveClip(moving, trackId, 1_040, edgeToleranceMs: 50);

        Assert.Equal(1_000, document.FindClip(moving).Clip.StartMs);

        document.MoveClip(moving, trackId, 250, edgeToleranceMs: 50);

        Assert.Equal(250, document.FindClip(moving).Clip.StartMs);
    }

    [Fact]
    public void ResizeClip_SnapsEndToNextClipButAllowsIntentionalOverlap()
    {
        var document = new MusicTimelineDocument(120);
        var trackId = document.Tracks[0].Id;
        var first = document.AddClip(trackId, "First", 0, 1_000);

        document.AddClip(trackId, "Next", 2_000, 1_000);

        document.ResizeClip(first, 0, 0, 1_960, edgeToleranceMs: 50);

        Assert.Equal(2_000, document.FindClip(first).Clip.DurationMs);

        document.ResizeClip(first, 0, 0, 2_500);

        Assert.Equal(2_500, document.FindClip(first).Clip.DurationMs);
    }

    [Fact]
    public void DisabledSnap_AllowsOverlap()
    {
        var document = new MusicTimelineDocument(120);
        var trackId = document.Tracks[0].Id;

        document.AddClip(trackId, "First", 0, 1_000);
        var moving = document.AddClip(trackId, "Moving", 3_000, 500);
        document.SetSnapEnabled(false);

        document.MoveClip(moving, trackId, 200, edgeToleranceMs: 100);

        Assert.Equal(200, document.FindClip(moving).Clip.StartMs);
    }

    [Fact]
    public void ReplaceMediaReferences_ChangesEveryVisibleOccurrenceAndCanUndo()
    {
        var replacement = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(replacement, [0]);
        try
        {
            var document = new MusicTimelineDocument();
            var firstTrack = document.Tracks[0].Id;
            var secondTrack = document.AddTrack();

            document.AddClip(firstTrack, "Original A", 0, 1_000, 42);
            document.AddClip(secondTrack, "Original B", 0, 1_000, 42);

            var count = document.ReplaceMediaReferences(42, replacement, 99, 12_345);

            Assert.Equal(2, count);
            Assert.All(document.Tracks.SelectMany(track => track.Clips), clip =>

            {

                Assert.Equal(replacement, clip.SourcePath);
                Assert.Equal(Path.GetFileNameWithoutExtension(replacement), clip.Name);
                Assert.Equal(99u, clip.ReplacementMediaId);
                Assert.Equal(12_345, clip.PhysicalDurationMs);
            });
            document.Undo();

            Assert.All(document.Tracks.SelectMany(track => track.Clips), clip => Assert.Null(clip.SourcePath));
        }
        finally
        {
            File.Delete(replacement);
        }
    }

    [Fact]
    public void LoadScope_KeepsOneSegmentCueSharedAcrossParallelTracks()
    {
        var validation = new BnkTimelineValidation(
            9,
            1,
            [
                new BnkTimelineSegment(100, 4_000, [101, 102], [new BnkTimelineMarker(43573010, 0)]),
                new BnkTimelineSegment(200, 8_000, [201], [new BnkTimelineMarker(43573010, 0)])
            ],
            [
                new BnkTimelineClip(101, 100, 1, 10, 0, 0, 0, 4_000, 0, 4_000, false),
                new BnkTimelineClip(201, 200, 2, 20, 0, 0, 0, 8_000, 0, 8_000, false)
            ],
            [],
            [],
            new BnkDurationValidation(9, [], []),
            []);
        var document = new MusicTimelineDocument();

        var result = MusicTimelineImporter.LoadScope(
            document,
            validation,
            120,
            new Dictionary<uint, string> { [1] = "Music\\Verse.wav", [2] = "Music\\Chorus.wav" },
            selectedSegmentId: 200);

        Assert.Equal(2, result.Segments);
        Assert.Equal(3, result.Tracks);
        Assert.Equal(2, result.Clips);
        Assert.Equal(8_000, document.TimelineLengthMs);
        Assert.Equal(200u, document.Tracks[0].SegmentObjectId);
        Assert.Equal("Chorus", document.Tracks[0].Name);
        Assert.Equal(8_000, document.Tracks[0].Clips[0].PhysicalDurationMs);
        Assert.Equal(2, document.Markers.Count);
        Assert.Equal([200u, 100u], document.Markers.Select(marker => marker.SegmentObjectId));
        Assert.Single(document.Markers, marker => marker.SegmentObjectId == 100);
        Assert.Equal(2, document.Tracks.Count(track => track.SegmentObjectId == 100));
    }

    [Fact]
    public void TrackMixControls_AreExclusiveAndUndoable()
    {
        var document = new MusicTimelineDocument();
        var first = document.Tracks[0].Id;
        var second = document.AddTrack("Layer");

        document.SetTrackMuted(first, true);
        document.SetTrackSolo(first, true);

        Assert.False(document.Tracks[0].IsMuted);
        Assert.True(document.Tracks[0].IsSolo);

        document.SetTrackSolo(second, true);
        document.SetTrackGain(second, 0.5);

        Assert.False(document.Tracks[0].IsMuted);
        Assert.False(document.Tracks[0].IsSolo);
        Assert.False(document.Tracks[1].IsMuted);
        Assert.True(document.Tracks[1].IsSolo);
        Assert.Equal(0.5, document.Tracks[1].Gain);

        document.Undo();

        Assert.Equal(1, document.Tracks[1].Gain);
    }

    [Fact]
    public void AddTrack_CanCreateAnImportedLaneBackedByAnAuthoredSegmentTrack()
    {
        var document = new MusicTimelineDocument(createDefaultTrack: false);

        var id = document.AddTrack("Imported song", objectId: 123, segmentObjectId: 456, lengthMs: 8_000);
        var track = Assert.Single(document.Tracks);

        Assert.Equal(id, track.Id);
        Assert.Equal(123u, track.ObjectId);
        Assert.Equal(456u, track.SegmentObjectId);
        Assert.Equal(8_000, track.LengthMs);
    }

    [Fact]
    public void InsertTrackAfter_PreservesTheChosenPositionAndWwiseScope()
    {
        var document = new MusicTimelineDocument();
        var first = document.Tracks[0].Id;
        var last = document.AddTrack("Last");

        var inserted = document.InsertTrackAfter(first, "New lane", 123, 456, 8_000);

        Assert.Equal([first, inserted, last], document.Tracks.Select(track => track.Id));
        Assert.Equal(123u, document.Tracks[1].ObjectId);
        Assert.Equal(456u, document.Tracks[1].SegmentObjectId);
        Assert.Equal(8_000, document.Tracks[1].LengthMs);
    }

    [Fact]
    public void SegmentTempo_OnlyScalesTheSelectedSegmentAndItsGrid()
    {
        var firstTrack = Guid.NewGuid();
        var secondTrack = Guid.NewGuid();
        var document = new MusicTimelineDocument(createDefaultTrack: false);

        document.Reset(
            120,
            8_000,
            [
                new MusicTimelineTrack(firstTrack, "First", [
                    new MusicTimelineClip(Guid.NewGuid(), 1, "A", null, 1_000, 200, 2_000)
                ], SegmentObjectId: 10, LengthMs: 4_000),
                new MusicTimelineTrack(secondTrack, "Second", [
                    new MusicTimelineClip(Guid.NewGuid(), 2, "B", null, 2_000, 300, 3_000)
                ], SegmentObjectId: 20, LengthMs: 8_000)
            ],
            [
                new MusicTimelineMarker(1, "Cue A", 2_000, 10),
                new MusicTimelineMarker(2, "Cue B", 3_000, 20)
            ]);

        document.SetSegmentBpmAndScale(10, 240);

        Assert.Equal(240, document.SegmentBpm(10));
        Assert.Equal(120, document.SegmentBpm(20));
        Assert.Equal(500, document.Tracks[0].Clips[0].StartMs);
        Assert.Equal(100, document.Tracks[0].Clips[0].SourceOffsetMs);
        Assert.Equal(1_000, document.Tracks[0].Clips[0].DurationMs);
        Assert.Equal(2_000, document.Tracks[1].Clips[0].StartMs);
        Assert.Equal(1_000, document.Markers[0].PositionMs);
        Assert.Equal(3_000, document.Markers[1].PositionMs);
        Assert.Equal(250, document.Snap(260, 10));
        Assert.Equal(500, document.Snap(260, 20));

        document.Undo();

        Assert.Equal(120, document.SegmentBpm(10));
        Assert.Equal(1_000, document.Tracks[0].Clips[0].StartMs);
    }

    [Fact]
    public void ReplaceMediaReferences_DoesNotOverwriteSeparatelyInsertedMedia()
    {
        var replacement = Path.GetTempFileName();
        try
        {
            var document = new MusicTimelineDocument();
            var track = document.Tracks[0].Id;

            document.AddClip(track, "Original", 0, 100, mediaId: 10);
            document.AddClip(
                track,
                "Inserted",
                100,
                100,
                mediaId: 10,
                sourcePath: replacement,
                replacementMediaId: 20);

            var count = document.ReplaceMediaReferences(10, replacement, 30, 100);

            Assert.Equal(1, count);
            Assert.Equal([30u, 20u], document.Tracks[0].Clips.Select(clip => clip.ReplacementMediaId));
        }
        finally
        {
            File.Delete(replacement);
        }
    }

    [Fact]
    public void SnapNear_IncludesAuthoredTrackEnd()
    {
        var document = new MusicTimelineDocument(createDefaultTrack: false);
        document.AddTrack("Song", segmentObjectId: 10, lengthMs: 4_125);

        var snapped = document.SnapNear(4_100, 30, 10);

        Assert.Equal(4_125, snapped);
    }

    [Fact]
    public void SetClipRenderedSource_KeepsPlacementAndNameWhileBakingArrangement()
    {
        var rendered = Path.GetTempFileName();
        try
        {
            var document = new MusicTimelineDocument(120);
            var track = document.Tracks[0].Id;
            var clipId = document.AddClip(track, "Original song", 500, 2_000, mediaId: 10);
            document.SetClipArrangement(clipId, 500, 250, 2_000, true, 100, 200);

            document.SetClipRenderedSource(clipId, rendered, 20, 2_375);

            var clip = document.FindClip(clipId).Clip;
            Assert.Equal("Original song", clip.Name);
            Assert.Equal(500, clip.StartMs);
            Assert.Equal(0, clip.SourceOffsetMs);
            Assert.Equal(2_375, clip.DurationMs);
            Assert.Equal(2_375, clip.PhysicalDurationMs);
            Assert.Equal(20u, clip.ReplacementMediaId);
            Assert.Equal(rendered, clip.SourcePath);
            Assert.False(clip.RepeatsSource);
            Assert.False(clip.HasFadeIn);
            Assert.False(clip.HasFadeOut);

            document.Undo();
            Assert.Null(document.FindClip(clipId).Clip.SourcePath);
        }
        finally
        {
            File.Delete(rendered);
        }
    }
}
