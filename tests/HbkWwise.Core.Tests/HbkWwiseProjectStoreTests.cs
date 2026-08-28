using HbkWwise.Core;

namespace HbkWwise.Core.Tests;

public sealed class HbkWwiseProjectStoreTests
{
    [Fact]
    public async Task RoundTripsCompositionAndTimeline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.hbkproj");
        var format = new MediaFormat("vgmstream", 48000, 2, 96000, "PCM", "stereo", 192000);

        var project = new HbkWwiseProject(
            HbkWwiseProject.CurrentVersion,
            @"C:\index.json",
            new HbkProjectComposition(10, 20, 30, 136.05),
            150,
            4,
            2,
            true,
            8000,
            [
                new HbkProjectTrack(
                    Guid.NewGuid(),
                    "Verse",
                    40,
                    20,
                    8000,
                    [
                        new HbkProjectClip(
                            Guid.NewGuid(),
                            "Guitar",
                            50,
                            @"C:\guitar.wav",
                            1000,
                            250,
                            2000,
                            60,
                            3000,
                            false,
                            new HbkProjectClipAnchor(40, 20, 3, 50),
                            250,
                            500)
                    ],
                    true,
                    false,
                    0.5)
            ],
            [new MusicTimelineMarker(1, "Entry", 1000, 20)],
            [new HbkProjectAudio(
                Guid.NewGuid(),
                "Guitar",
                @"C:\guitar.flac",
                format,
                @"C:\Project_audio\Converted\guitar.wav")],
            [new HbkProjectReplacement(50, 60, @"C:\guitar.wav", 3000)],
            [new HbkProjectImport(50, 70, @"C:\other.wav", 4000)],
            [new HbkProjectSegmentTempo(20, 164)],
            [
                new HbkProjectTimeline(
                    Guid.NewGuid(),
                    "Music_ST01",
                    new HbkProjectComposition(10, 20, 30, 136.05),
                    150,
                    4,
                    2,
                    true,
                    8000,
                    [],
                    [new MusicTimelineMarker(1, "Entry", 1000, 20, [128])],
                    [],
                    [],
                    [new HbkProjectSegmentTempo(20, 164)],
                    [20],
                    [20],
                    50,
                    10,
                    70,
                    "Music_ST01")
            ],
            null,
            ["media:Music_ST01:50"],
            [20],
            [new HbkProjectGeneratedAudio(
                @"C:\Project_audio\Generated\guitar.wav",
                @"C:\guitar.flac",
                125,
                50,
                2000,
                FadeInMs: 100,
                FadeOutMs: 200)]);

        try
        {
            await HbkWwiseProjectStore.SaveAsync(project, path);
            var loaded = await HbkWwiseProjectStore.LoadAsync(path);

            Assert.Equal(project.Version, loaded.Version);
            Assert.Equal(project.Composition, loaded.Composition);
            Assert.Equal(project.Tracks[0].Clips[0], loaded.Tracks[0].Clips[0]);
            Assert.Equal(project.Tracks[0].IsMuted, loaded.Tracks[0].IsMuted);
            Assert.Equal(project.Tracks[0].Gain, loaded.Tracks[0].Gain);
            Assert.Equal(project.Markers[0], loaded.Markers[0]);
            Assert.Equal(project.ImportedAudio[0], loaded.ImportedAudio[0]);
            Assert.Equal(project.Replacements[0], loaded.Replacements[0]);
            Assert.Equal(project.Imports[0], loaded.Imports[0]);
            Assert.Equal(164, Assert.Single(loaded.SegmentTempos!).Bpm);
            Assert.Equal("Music_ST01", Assert.Single(loaded.Timelines!).Name);
            Assert.Equal([20u], loaded.Timelines![0].MetronomeSegments!);
            Assert.Equal([20u], loaded.Timelines[0].VisibleSegmentIds!);
            Assert.Equal(50u, loaded.Timelines[0].OccurrenceMediaId);
            Assert.Equal(10u, loaded.Timelines[0].InspectionEventId);
            Assert.Equal(70u, loaded.Timelines[0].StandaloneMediaId);
            Assert.Equal("Music_ST01", loaded.Timelines[0].StandaloneMediaBank);
            Assert.Equal([128], loaded.Timelines[0].Markers[0].PositionOffsets!);
            Assert.Equal(["media:Music_ST01:50"], loaded.PinnedClipKeys!);
            Assert.Equal(project.GeneratedAudio![0], Assert.Single(loaded.GeneratedAudio!));
            Assert.Contains("\"composition\"", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsUnknownVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.hbkproj");
        await File.WriteAllTextAsync(path, """
            { "version": 99, "bpm": 120, "beatsPerBar": 4, "subdivisionsPerBeat": 1,
              "snapEnabled": true, "tracks": [], "markers": [], "importedAudio": [],
              "replacements": [], "imports": [] }
            """);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => HbkWwiseProjectStore.LoadAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OlderVersionOneProjectDefaultsTrackMixState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.hbkproj");
        await File.WriteAllTextAsync(path, $$"""
            { "version": 1, "bpm": 120, "beatsPerBar": 4, "subdivisionsPerBeat": 1,
              "snapEnabled": true, "timelineLengthMs": 1000,
              "tracks": [{ "id": "{{Guid.NewGuid()}}", "name": "Track", "clips": [] }],
              "markers": [], "importedAudio": [], "replacements": [], "imports": [] }
            """);

        try
        {
            var project = await HbkWwiseProjectStore.LoadAsync(path);

            Assert.False(project.Tracks[0].IsMuted);
            Assert.False(project.Tracks[0].IsSolo);
            Assert.Equal(1, project.Tracks[0].Gain);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveRejectsNonFiniteTimelineLength()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.hbkproj");
        var project = MinimalProject() with { TimelineLengthMs = double.NaN };

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => HbkWwiseProjectStore.SaveAsync(project, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveRejectsInvalidClipInsideTimelineTab()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.hbkproj");
        var timeline = new HbkProjectTimeline(
            Guid.NewGuid(),
            "Invalid tab",
            null,
            120,
            4,
            1,
            true,
            1000,
            [
                new HbkProjectTrack(
                    Guid.NewGuid(),
                    "Track",
                    null,
                    null,
                    1000,
                    [
                        new HbkProjectClip(
                            Guid.NewGuid(),
                            "Broken clip",
                            null,
                            null,
                            0,
                            0,
                            0,
                            null,
                            null,
                            false,
                            null)
                    ])
            ],
            [],
            [],
            [],
            []);
        var project = MinimalProject([timeline]);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => HbkWwiseProjectStore.SaveAsync(project, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RestoreRebindsBinaryOffsetsFromFreshValidation()
    {
        var clipId = Guid.NewGuid();
        var project = new HbkWwiseProject(
            1,
            null,
            new HbkProjectComposition(1, 2, 3, 120),
            120,
            4,
            1,
            true,
            4000,
            [
                new HbkProjectTrack(
                    Guid.NewGuid(),
                    "Track",
                    10,
                    2,
                    4000,
                    [
                        new HbkProjectClip(
                            clipId,
                            "Edited",
                            20,
                            null,
                            500,
                            100,
                            1500,
                            null,
                            2000,
                            false,
                            new HbkProjectClipAnchor(10, 2, 4, 20),
                            300,
                            400)
                    ])
            ],
            [],
            [],
            [],
            []);
        var fields = new BnkTimelineFieldOffsets(101, 102, 103, 104);
        var validation = new BnkTimelineValidation(
            3,
            1,
            [new BnkTimelineSegment(2, 4000, [10], [])],
            [
                new BnkTimelineClip(
                    10,
                    2,
                    20,
                    999,
                    0,
                    0,
                    0,
                    2222,
                    0,
                    2000,
                    false,
                    fields,
                    4,
                    FadeInMs: 100,
                    FadeOutMs: 200)
            ],
            [],
            [],
            new BnkDurationValidation(3, [], []),
            []);

        var restored = HbkWwiseProjectTimeline.RestoreTracks(project, validation)[0].Clips[0];

        Assert.Equal(clipId, restored.Id);
        Assert.Equal(500, restored.StartMs);
        Assert.Equal(999, restored.SourceIdOffset);
        Assert.Equal(fields, restored.FieldOffsets);
        Assert.Equal(2222, restored.PhysicalDurationMs);
        Assert.Equal(300, restored.FadeInMs);
        Assert.Equal(400, restored.FadeOutMs);
    }

    private static HbkWwiseProject MinimalProject(HbkProjectTimeline[]? timelines = null) => new(
        HbkWwiseProject.CurrentVersion,
        null,
        null,
        120,
        4,
        1,
        true,
        1000,
        [],
        [],
        [],
        [],
        [],
        SegmentTempos: [],
        Timelines: timelines);
}
