using HbkWwise.Core;
using System.Text;

namespace HbkWwise.Core.Tests;

public sealed class HbkWwiseProjectAssetsTests
{
    [Fact]
    public async Task RepairFindsMovedSourceAndRegeneratesMissingAlignedAudioBesideProject()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"hbk-project-repair-{Guid.NewGuid():N}");
        var projectPath = Path.Combine(temporary, "SongMod.hbkproj");
        var movedSource = Path.Combine(temporary, "Color Your Night.wav");
        var missingSource = Path.Combine(temporary, "Old", "16. Color Your Night.wav");
        var generated = Path.Combine(
            HbkWwiseProjectAssets.AudioRoot(projectPath), "Generated", "aligned.wav");

        Directory.CreateDirectory(temporary);
        WriteMono(movedSource, 48_000, 4_800);

        var format = new MediaFormat("test", 48_000, 1, 4_800, "PCM", "mono", null);
        var clip = new HbkProjectClip(
            Guid.NewGuid(), "16. Color Your Night", 10, generated,
            0, 0, 150, 20, 150, false, null);

        var copiedClip = clip with { Id = Guid.NewGuid(), ReplacementMediaId = 21 };
        var project = new HbkWwiseProject(
            1, null, null, 120, 4, 1, true, 1000,
            [new HbkProjectTrack(Guid.NewGuid(), "Track", null, null, 1000, [clip, copiedClip])],
            [],
            [new HbkProjectAudio(Guid.NewGuid(), "16. Color Your Night", missingSource, format)],
            [],
            [new HbkProjectImport(10, 20, generated, 150),
             new HbkProjectImport(10, 21, generated, 150)]);

        try
        {
            var repaired = await HbkWwiseProjectAudio.RepairAsync(project, projectPath, null);

            Assert.Equal(1, repaired.RebuiltFiles);
            Assert.Equal(1, repaired.DeduplicatedMedia);
            Assert.True(repaired.NeedsSave);
            Assert.True(File.Exists(generated));
            Assert.Equal(50, Assert.Single(repaired.Project.GeneratedAudio!).LeadingSilenceMs);
            Assert.StartsWith(
                Path.Combine(HbkWwiseProjectAssets.AudioRoot(projectPath), "Sources"),
                repaired.Project.ImportedAudio[0].Path,
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(repaired.Project.Imports);
            Assert.All(
                repaired.Project.Tracks[0].Clips,
                item => Assert.Equal(20u, item.ReplacementMediaId));
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    [Fact]
    public void LocalizeCopiesOwnedAudioBesideSaveAsProjectAndRewritesReferences()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"hbk-project-assets-{Guid.NewGuid():N}");
        var oldProject = Path.Combine(temporary, "Old", "Old.hbkproj");
        var oldAudio = Path.Combine(HbkWwiseProjectAssets.AudioRoot(oldProject), "Generated", "song.wav");
        var sourceAudio = Path.Combine(temporary, "Imports", "song.flac");
        var sameNameSource = Path.Combine(temporary, "OtherImports", "song.flac");
        var newProject = Path.Combine(temporary, "New", "SongMod.hbkproj");

        Directory.CreateDirectory(Path.GetDirectoryName(oldAudio)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceAudio)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sameNameSource)!);
        File.WriteAllBytes(oldAudio, [1, 2, 3]);
        File.WriteAllBytes(sourceAudio, [4, 5, 6]);
        File.WriteAllBytes(sameNameSource, [6, 5, 4]);

        var clip = new HbkProjectClip(
            Guid.NewGuid(), "Song", 10, oldAudio, 0, 0, 1000, 20, 1000, false, null);
        var project = new HbkWwiseProject(
            1, null, null, 120, 4, 1, true, 1000,
            [new HbkProjectTrack(Guid.NewGuid(), "Track", null, null, 1000, [clip])],
            [],
            [new HbkProjectAudio(
                Guid.NewGuid(), "Song", sourceAudio,
                new MediaFormat("test", 48_000, 2, 48_000, "PCM", "stereo", null)),
             new HbkProjectAudio(
                Guid.NewGuid(), "Other Song", sameNameSource,
                new MediaFormat("test", 48_000, 2, 48_000, "PCM", "stereo", null))],
            [],
            [new HbkProjectImport(10, 20, oldAudio, 1000)],
            GeneratedAudio: [new HbkProjectGeneratedAudio(oldAudio, @"C:\Music\song.flac", 100, 0, 900)]);

        try
        {
            var result = HbkWwiseProjectAssets.LocalizeWithMap(project, newProject, oldProject);
            var expected = Path.Combine(
                HbkWwiseProjectAssets.AudioRoot(newProject),
                "Generated",
                "song.wav");

            Assert.Equal(expected, result.Project.Tracks[0].Clips[0].SourcePath);
            Assert.Equal(expected, result.Project.Imports[0].Path);
            Assert.Equal(expected, result.Project.GeneratedAudio![0].Path);
            Assert.Equal([1, 2, 3], File.ReadAllBytes(expected));
            Assert.Equal(expected, result.PathMap[Path.GetFullPath(oldAudio)]);
            Assert.Equal(
                Path.Combine(HbkWwiseProjectAssets.AudioRoot(newProject), "Sources", "song.flac"),
                result.Project.ImportedAudio[0].Path);
            Assert.NotEqual(
                result.Project.ImportedAudio[0].Path,
                result.Project.ImportedAudio[1].Path);
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static void WriteMono(string path, int sampleRate, int frames)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        var dataSize = frames * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var index = 0; index < frames; index++)
        {
            writer.Write((short)8_000);
        }
    }
}
