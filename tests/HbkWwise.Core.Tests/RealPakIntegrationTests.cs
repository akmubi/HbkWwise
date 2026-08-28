using System.Buffers.Binary;
using HbkWwise.Core;
namespace HbkWwise.Core.Tests;

public sealed class RealPakIntegrationTests
{
    internal const string PakDirectory = @"C:\Program Files (x86)\Steam\steamapps\common\Hi-Fi RUSH\Hibiki\Content\Paks";
    internal const string VgmstreamPath = @"C:\Users\akmubi\Documents\HibikiMods\AudioMods\vgmstream-win\vgmstream-cli.exe";
    internal const string WwiserPath = @"C:\Users\akmubi\Documents\HibikiMods\AudioMods\wwiser.pyz";
    internal const string WwiseConsolePath = @"C:\Audiokinetic\Wwise_2019.2.15.7667\Authoring\x64\Release\bin\WwiseConsole.exe";

    [RealPakFact]
    [Trait("Category", "Integration")]
    public async Task ConfiguredRepak_FallsBackForUpdateArchiveLayout()
    {
        var key = Environment.GetEnvironmentVariable("HBKWWISE_AES_KEY")!;
        var directory = Environment.GetEnvironmentVariable("HBKWWISE_PAK_DIR") ?? PakDirectory;
        var updatePak = Environment.GetEnvironmentVariable("HBKWWISE_UPDATE_PAK")
            ?? Path.Combine(directory, "Hibiki-WindowsNoEditor_0_P.pak");
        var configuredRepak = Environment.GetEnvironmentVariable("HBKWWISE_CONFIGURED_REPAK")
            ?? @"C:\Program Files\repak_cli\bin\repak.exe";
        var source = new PakSource(updatePak, "Hibiki/Content/WwiseAudio/Windows", 1);
        var first = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wem");
        var second = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wem");

        try
        {
            await RepakArchive.ExtractEntryAsync(
                [source],
                "Hibiki/Content/WwiseAudio/Windows/1017155452.wem",
                first,
                configuredRepak,
                key);
            await RepakArchive.ExtractEntryAsync(
                [source],
                "Hibiki/Content/WwiseAudio/Windows/1017155452.wem",
                second,
                configuredRepak,
                key);

            Assert.True(new FileInfo(first).Length > 10_000_000);
            Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [RealPakFact]
    [Trait("Category", "Integration")]
    public async Task BaseAndUpdatePaks_OverlayAndExtractEffectiveAssets()
    {
        var key = Environment.GetEnvironmentVariable("HBKWWISE_AES_KEY")!;
        var directory = Environment.GetEnvironmentVariable("HBKWWISE_PAK_DIR") ?? PakDirectory;
        var basePak = Environment.GetEnvironmentVariable("HBKWWISE_BASE_PAK")
            ?? Path.Combine(directory, "Hibiki-WindowsNoEditor.pak");
        var updatePak = Environment.GetEnvironmentVariable("HBKWWISE_UPDATE_PAK")
            ?? Environment.GetEnvironmentVariable("HBKWWISE_DLC_PAK")
            ?? Path.Combine(directory, "Hibiki-WindowsNoEditor_0_P.pak");
        var cache = Path.Combine(Path.GetTempPath(), "hbkwwise-real-pak-cache");
        var index = await RepakArchive.BuildIndexAsync([basePak, updatePak], cache, aesKey: key);

        Assert.Equal(2, index.Paks?.Length);
        Assert.Equal([0, 1], index.Paks!.Select(pak => pak.Priority));
        Assert.Contains(index.Banks, bank => bank.Name == "Music_ST01");
        Assert.Contains(index.FindMedia(6639526), media => media.Bank == "Music_ST01" && media.PrefetchSize == 26732);
        Assert.Contains(index.FindMedia(100001412), media => media.SourceName.Contains("wm_ie_ev2000_02", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(index.FindMedia(428446315), media => media.IsEmbedded && media.Bank == "Ambient_St07");
        Assert.Contains(index.Overrides(), item => item.EntryPath.EndsWith("/1017155452.wem", StringComparison.OrdinalIgnoreCase));

        var baseWem = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wem");
        var exportedStreamedWem = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wem");
        var updateWem = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wem");
        var overlaidWem = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wem");
        var directUpdateWem = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wem");
        var embeddedWem = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wem");
        var bnk = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bnk");
        var st06Bnk = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bnk");
        var st06Xml = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        var exactOwnerBnk = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bnk");
        var bankXml = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        var rewrittenBnk = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bnk");
        var rewrittenXml = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        var modPak = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pak");
        var retimeModPak = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pak");
        var shortModPak = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pak");
        var scopedModPak = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pak");
        var segmentScopedModPak = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pak");
        var replacementWav = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
        var retimedBnk = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bnk");
        var retimedXml = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        var structuredBnk = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bnk");
        var structuredXml = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        var segmentRetimedBnk = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bnk");
        var segmentRetimedXml = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        var commonBnk = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bnk");
        var commonXml = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        var sameBankModPak = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pak");

        try
        {
            await RepakArchive.ExtractMediaAsync(index.Paks!, 6639526, baseWem, aesKey: key);
            await MediaExtractor.ExtractAsync(index, 6639526, exportedStreamedWem, aesKey: key);
            await RepakArchive.ExtractMediaAsync(index.Paks!, 100001412, updateWem, aesKey: key);
            await RepakArchive.ExtractMediaAsync(index.Paks!, 1017155452, overlaidWem, aesKey: key);
            await RepakArchive.ExtractMediaAsync([index.Paks![1]], 1017155452, directUpdateWem, aesKey: key);
            await MediaExtractor.ExtractAsync(index, 428446315, embeddedWem, aesKey: key);
            await RepakArchive.ExtractBankAsync(index.Paks!, "Music_ST01", bnk, aesKey: key);
            await RepakArchive.ExtractBankAsync(index.Paks!, "Music_ST06", st06Bnk, aesKey: key);
            var enemyBank = index.Banks.First(bank => bank.Name == "Enm_em7500_InstFx");
            var enemyAsset = enemyBank.EffectiveAsset()!;
            var enemyOwner = index.Paks!.Single(pak => Path.GetFullPath(pak.Path)
                .Equals(Path.GetFullPath(enemyAsset.PakPath), StringComparison.OrdinalIgnoreCase));

            await RepakArchive.ExtractEntryAsync([enemyOwner], enemyAsset.EntryPath, exactOwnerBnk, aesKey: key);
            _ = BnkFile.Read(exactOwnerBnk);

            Assert.True(new FileInfo(baseWem).Length > 1_000_000);
            Assert.Equal(await File.ReadAllBytesAsync(baseWem), await File.ReadAllBytesAsync(exportedStreamedWem));
            Assert.True(new FileInfo(updateWem).Length > 10_000);
            Assert.Equal(await File.ReadAllBytesAsync(directUpdateWem), await File.ReadAllBytesAsync(overlaidWem));
            Assert.True(new FileInfo(bnk).Length > 100_000);

            await WwiserClient.DumpXmlAsync(
                st06Bnk,
                st06Xml,
                Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"));
            var inheritedSt06Scope = Assert.Single(
                BnkRetimer.FindTimingScopes(st06Xml, "HBK_ST06_KORSICA_SLOWFX_00_Play"));

            Assert.Equal(106761397u, inheritedSt06Scope.ObjectId);
            Assert.Equal([144], inheritedSt06Scope.Bpms, new DoubleComparer(0.001));
            Assert.Contains(968324533u, inheritedSt06Scope.ObjectIds);

            var parsedBank = BnkFile.Read(bnk);

            Assert.True(parsedBank.TryGetMedia(6639526, out var prefetch));
            Assert.Equal(BnkMediaKind.Prefetch, prefetch.Kind);
            Assert.Equal(26732, prefetch.Size);

            await WwiserClient.DumpXmlAsync(
                bnk,
                bankXml,
                Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"));
            var graph = WwiserHircGraph.Load(bankXml);
            var scope = graph.EventScope("Mu_Session_ST01A_Stop");

            Assert.Equal([101117670u], scope.ActionIds);
            Assert.Equal([626869950u], scope.TargetIds);
            Assert.Equal(168, scope.ReachableObjectIds.Length);
            Assert.Equal(39, scope.Media.Length);

            var timingScopes = BnkRetimer.FindTimingScopes(bankXml, "Mu_Session_ST01A_Play");
            var timingScope = Assert.Single(timingScopes, item => item.ObjectId == 626869950);

            Assert.Equal([136.05], timingScope.Bpms, new DoubleComparer(0.001));
            Assert.Equal(138, timingScope.RetimeObjects);
            Assert.Equal(32, timingScope.MediaIds.Length);

            var originalTimeline = BnkTimelineValidator.Validate(
                bankXml,
                timingScope.ObjectId,
                new Dictionary<uint, double>(),
                136.05,
                136.05,
                eventNameOrId: "Mu_Session_ST01A_Play");

            Assert.False(originalTimeline.HasErrors);
            Assert.Equal(48, originalTimeline.Segments.Length);
            Assert.Equal(80, originalTimeline.Clips.Length);
            Assert.Equal(68, originalTimeline.Transitions.Length);
            Assert.Equal(81, originalTimeline.Loops.Length);

            var st01bScope = Assert.Single(
                BnkRetimer.FindTimingScopes(bankXml, "Mu_Session_ST01B_Play"),
                item => item.ObjectIds.Contains(866586821u));
            var st01bTimeline = BnkTimelineValidator.Validate(
                bankXml,
                st01bScope.ObjectId,
                new Dictionary<uint, double>(),
                st01bScope.Bpms.Single(),
                st01bScope.Bpms.Single(),
                eventNameOrId: "Mu_Session_ST01B_Play");
            var verseOccurrences = st01bTimeline.Clips
                .Where(clip => clip.MediaId == 50807738 && clip.SegmentObjectId is not null)
                .ToArray();

            Assert.NotEmpty(verseOccurrences);
            Assert.True(verseOccurrences.Select(clip => clip.SegmentObjectId).Distinct().Count() > 1);

            var authoredCrossfade = st01bTimeline.Clips
                .Where(clip => clip.TrackObjectId == 866586821)
                .OrderBy(clip => clip.PlaylistIndex)
                .ToArray();

            Assert.Equal(2, authoredCrossfade.Length);
            Assert.All(authoredCrossfade, clip => Assert.Equal(1035343253u, clip.MediaId));
            Assert.Equal([0, 1], authoredCrossfade.Select(clip => clip.PlaylistIndex));
            Assert.True(authoredCrossfade[0].HasFadeOut);
            Assert.True(authoredCrossfade[1].HasFadeIn);
            Assert.True(authoredCrossfade[1].TimelineStartMs < authoredCrossfade[0].TimelineEndMs);

            var structural = BnkTimelineStructureEditor.Apply(
                await File.ReadAllBytesAsync(bnk),
                bankXml,
                [new BnkTrackPlaylistEdit(866586821, authoredCrossfade.Select((clip, index) => index == 0
                        ? PlaylistEdit(clip) with { Fades = new BnkClipFadeEdit(123, 456) }
                        : PlaylistEdit(clip))
                    .Append(PlaylistEdit(authoredCrossfade[0]) with
                    {
                        StartMs = authoredCrossfade[0].TimelineStartMs + 1
                    }).ToArray())]);
            await File.WriteAllBytesAsync(structuredBnk, structural.Data);
            _ = BnkFile.Read(structuredBnk);
            await WwiserClient.DumpXmlAsync(
                structuredBnk,
                structuredXml,
                Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"));
            var structuredTimeline = BnkTimelineValidator.Validate(
                structuredXml,
                st01bScope.ObjectId,
                new Dictionary<uint, double>(),
                st01bScope.Bpms.Single(),
                st01bScope.Bpms.Single(),
                eventNameOrId: "Mu_Session_ST01B_Play");
            var structuredCrossfade = structuredTimeline.Clips
                .Where(clip => clip.TrackObjectId == 866586821)
                .OrderBy(clip => clip.PlaylistIndex)
                .ToArray();

            Assert.Equal(3, structuredCrossfade.Length);
            Assert.True(structuredCrossfade[0].HasFadeOut);
            Assert.Equal(123, structuredCrossfade[0].FadeInMs, 2);
            Assert.Equal(456, structuredCrossfade[0].FadeOutMs, 2);
            Assert.True(structuredCrossfade[1].HasFadeIn);
            Assert.False(structuredCrossfade[2].HasFadeIn);
            Assert.False(structuredCrossfade[2].HasFadeOut);

            var knownTimelineClip = originalTimeline.Clips.First(clip => clip.MediaId == 6639526 && clip.SegmentObjectId is not null);
            var segmentPlan = BnkRetimer.PlanSegmentOverride(
                await File.ReadAllBytesAsync(bnk),
                bankXml,
                timingScope.ObjectId,
                knownTimelineClip.SegmentObjectId!.Value,
                164,
                136.05,
                eventNameOrId: "Mu_Session_ST01A_Play");

            Assert.Contains(segmentPlan.Patches, patch => patch.Kind == "meter-enable");
            Assert.DoesNotContain(segmentPlan.RetimeObjectIds, id => id == timingScope.ObjectId);

            await File.WriteAllBytesAsync(
                segmentRetimedBnk,
                BnkRetimer.Apply(await File.ReadAllBytesAsync(bnk), segmentPlan));
            await WwiserClient.DumpXmlAsync(
                segmentRetimedBnk,
                segmentRetimedXml,
                Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"));
            var independentSegmentScope = Assert.Single(
                BnkRetimer.FindTimingScopes(segmentRetimedXml, "Mu_Session_ST01A_Play"),
                item => item.ObjectId == knownTimelineClip.SegmentObjectId);

            Assert.Equal([164], independentSegmentScope.Bpms, new DoubleComparer(0.001));

            var editor = new MusicTimelineDocument();
            var imported = MusicTimelineImporter.LoadSegment(
                editor,
                originalTimeline,
                knownTimelineClip.SegmentObjectId!.Value,
                timingScope.Bpms[0],
                index.Media.GroupBy(media => media.Id).ToDictionary(group => group.Key, group => group.First().SourceName));

            Assert.Contains(editor.Tracks.SelectMany(track => track.Clips), clip => clip.MediaId == 6639526);
            Assert.Equal(originalTimeline.Segments.Single(segment => segment.ObjectId == imported.SegmentObjectId).DurationMs,
                editor.TimelineLengthMs);

            Assert.NotEmpty(editor.Markers);

            var compositionEditor = new MusicTimelineDocument();
            var composition = MusicTimelineImporter.LoadScope(
                compositionEditor,
                originalTimeline,
                timingScope.Bpms[0],
                index.Media.GroupBy(media => media.Id).ToDictionary(group => group.Key, group => group.First().SourceName),
                selectedSegmentId: knownTimelineClip.SegmentObjectId);

            Assert.Equal(48, composition.Segments);
            Assert.Equal(80, composition.Clips);
            Assert.Equal(80, compositionEditor.Tracks.Sum(track => track.Clips.Length));
            Assert.Contains(compositionEditor.Tracks.SelectMany(track => track.Clips), clip => clip.MediaId == 6639526);

            var fasterTimeline = BnkTimelineValidator.Validate(
                bankXml,
                timingScope.ObjectId,
                new Dictionary<uint, double>(),
                136.05,
                164,
                eventNameOrId: "Mu_Session_ST01A_Play");

            Assert.True(fasterTimeline.HasErrors);
            Assert.Contains(fasterTimeline.Issues, item => item.Code == "CLIP_BOUNDS" && item.MediaId == 6639526);

            var retimePlan = BnkRetimer.Plan(
                await File.ReadAllBytesAsync(bnk),
                bankXml,
                timingScope.ObjectId,
                164,
                136.05,
                eventNameOrId: "Mu_Session_ST01A_Play");

            Assert.Equal(626869950u, retimePlan.ScopeObjectId);
            Assert.Equal(262, retimePlan.Patches.Length);
            Assert.Equal(32, retimePlan.AffectedMediaIds.Length);
            Assert.Equal(138, retimePlan.RetimeObjectIds.Length);

            var knownTrim = Assert.Single(retimePlan.Patches, patch =>
                patch.Kind == "track-begin-trim" && Math.Abs(patch.OldValue - 95_259.0959206174) < 0.000001);

            Assert.Equal(95_259.0959206174 * (double)136.05f / 164, knownTrim.NewValue, 6);

            await File.WriteAllBytesAsync(retimedBnk, BnkRetimer.Apply(await File.ReadAllBytesAsync(bnk), retimePlan));
            _ = BnkFile.Read(retimedBnk);
            await WwiserClient.DumpXmlAsync(
                retimedBnk,
                retimedXml,
                Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"));
            var appliedPlan = BnkRetimer.Plan(
                await File.ReadAllBytesAsync(retimedBnk),
                retimedXml,
                timingScope.ObjectId,
                164,
                164,
                eventNameOrId: "Mu_Session_ST01A_Play");

            Assert.Equal(retimePlan.ScopeObjectId, appliedPlan.ScopeObjectId);
            Assert.Empty(appliedPlan.Patches);

            const uint resizedMediaId = 935691395;
            var originalEmbedded = parsedBank.ExtractCompleteMedia(resizedMediaId);
            var resizedEmbedded = new byte[originalEmbedded.Length + 16];

            originalEmbedded.CopyTo(resizedEmbedded, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(resizedEmbedded.AsSpan(4), (uint)resizedEmbedded.Length - 8);
            var memoryOffsets = graph.MemorySizeOffsets(resizedMediaId);

            Assert.NotEmpty(memoryOffsets);

            var rewrite = parsedBank.RewriteMedia(
                new Dictionary<uint, byte[]> { [resizedMediaId] = resizedEmbedded },
                new Dictionary<uint, int[]> { [resizedMediaId] = memoryOffsets });
            await File.WriteAllBytesAsync(rewrittenBnk, rewrite.Data);

            Assert.Equal(resizedEmbedded, BnkFile.Read(rewrittenBnk).ExtractCompleteMedia(resizedMediaId));

            await WwiserClient.DumpXmlAsync(
                rewrittenBnk,
                rewrittenXml,
                Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"));
            var rewrittenSizes = WwiserHircGraph.Load(rewrittenXml).Objects.Values
                .SelectMany(item => item.Media)
                .Where(item => item.MediaId == resizedMediaId)
                .Select(item => item.MemorySize)
                .OfType<int>()
                .Distinct()
                .ToArray();

            Assert.Equal([resizedEmbedded.Length], rewrittenSizes);

            var mod = await ModPakBuilder.BuildAsync(
                index,
                new Dictionary<uint, string> { [6639526] = baseWem },
                modPak,
                aesKey: key,
                wwiserPath: Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                pythonPath: Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"));

            Assert.Equal(["Add_Music_RD", "Music_RhythmTower", "Music_ST01"], mod.Banks);
            Assert.Empty(mod.Retimes);
            Assert.Null(mod.DurationValidation);
            Assert.Null(mod.TimelineValidation);

            await Assert.ThrowsAsync<InvalidDataException>(() => ModPakBuilder.BuildAsync(
                index,
                new Dictionary<uint, string> { [6639526] = baseWem },
                retimeModPak,
                aesKey: key,
                wwiserPath: Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                pythonPath: Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"),
                retime: new ModPakRetimeRequest(
                    "Mu_Session_ST01A_Play",
                    timingScope.ObjectId,
                    136.05,
                    164),
                vgmstreamPath: Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM") ?? VgmstreamPath));

            Assert.False(File.Exists(retimeModPak));

            await Assert.ThrowsAsync<InvalidDataException>(() => ModPakBuilder.BuildAsync(
                index,
                new Dictionary<uint, string> { [6639526] = embeddedWem },
                shortModPak,
                aesKey: key,
                wwiserPath: Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                pythonPath: Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"),
                retime: new ModPakRetimeRequest(
                    "Mu_Session_ST01A_Play",
                    timingScope.ObjectId,
                    136.05,
                    164),
                vgmstreamPath: Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM") ?? VgmstreamPath));

            Assert.False(File.Exists(shortModPak));
            Assert.Equal(4, mod.Entries.Length);
            Assert.Contains(mod.Entries, entry => entry.EndsWith("/Add_Music_RD.bnk", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(mod.Entries, entry => entry.EndsWith("/Music_RhythmTower.bnk", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(mod.Entries, entry => entry.EndsWith("/Music_ST01.bnk", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(mod.Entries, entry => entry.EndsWith("/6639526.wem", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(mod.Entries, (await RepakArchive.ListAsync(modPak)).Order(StringComparer.OrdinalIgnoreCase));

            await VgmstreamClient.DecodeAsync(
                baseWem,
                replacementWav,
                Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM") ?? VgmstreamPath);
            var waveform = WaveformAnalyzer.Analyze(replacementWav, 256);

            Assert.Equal(256, waveform.Points);
            Assert.True(waveform.DurationMs > 1_000);
            Assert.Contains(waveform.Maximums, value => value > 0.01f);

            await RepakArchive.ExtractBankAsync(index.Paks!, "Music_Common", commonBnk, aesKey: key);
            await WwiserClient.DumpXmlAsync(
                commonBnk,
                commonXml,
                Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"));
            var commonScopes = BnkRetimer.FindTimingScopes(
                    commonXml,
                    "Mu_Session_00_M90_HIDEOUT_01_Play")
                .Where(scope => scope.Bpms.Length == 1)
                .Select(scope => (
                    Scope: scope,
                    Timeline: BnkTimelineValidator.Validate(
                        commonXml,
                        scope.ObjectId,
                        new Dictionary<uint, double>(),
                        scope.Bpms[0],
                        scope.Bpms[0],
                        eventNameOrId: "Mu_Session_00_M90_HIDEOUT_01_Play")))
                .Where(item => item.Timeline.Clips.Any(clip => clip.SourceIdOffset is not null))
                .ToArray();
            Assert.True(commonScopes.Length >= 2);

            var structuralScope = commonScopes.MinBy(item => item.Timeline.Clips
                .Where(clip => clip.SourceIdOffset is not null)
                .Min(clip => clip.SourceIdOffset!.Value));

            var fieldScope = commonScopes
                .Where(item => item.Scope.ObjectId != structuralScope.Scope.ObjectId)
                .MaxBy(item => item.Timeline.Clips
                    .Where(clip => clip.SourceIdOffset is not null)
                    .Max(clip => clip.SourceIdOffset!.Value));

            var structuralTrack = structuralScope.Timeline.Clips
                .Where(clip => clip.SourceIdOffset is not null)
                .GroupBy(clip => clip.TrackObjectId)
                .MinBy(group => group.Min(clip => clip.SourceIdOffset!.Value))!;

            var structuralTemplate = structuralTrack.OrderBy(clip => clip.PlaylistIndex).First();
            var inserted = PlaylistEdit(structuralTemplate) with
            {
                PreserveAutomation = false
            };

            var fieldClip = fieldScope.Timeline.Clips
                .Where(clip => clip.SourceIdOffset is not null)
                .MaxBy(clip => clip.SourceIdOffset!.Value)!;
            var sameBank = await ProjectModPakBuilder.BuildAsync(
                index,
                [
                    new ScopedModPakRequest(
                        "Mu_Session_00_M90_HIDEOUT_01_Play",
                        structuralScope.Scope.ObjectId,
                        structuralScope.Scope.Bpms[0],
                        structuralScope.Scope.Bpms[0],
                        [],
                        PlaylistEdits:
                        [
                            new BnkTrackPlaylistEdit(
                                structuralTemplate.TrackObjectId,
                                structuralTrack.OrderBy(clip => clip.PlaylistIndex)
                                    .Select(clip => PlaylistEdit(clip))
                                    .Append(inserted)
                                    .ToArray())
                        ]),
                    new ScopedModPakRequest(
                        "Mu_Session_00_M90_HIDEOUT_01_Play",
                        fieldScope.Scope.ObjectId,
                        fieldScope.Scope.Bpms[0],
                        fieldScope.Scope.Bpms[0],
                        [],
                        TimelineEdits:
                        [
                            new BnkTimelineClipEdit(
                                fieldClip.SourceIdOffset!.Value,
                                Math.Max(0, fieldClip.TimelineStartMs) + 1,
                                Math.Max(0, fieldClip.BeginTrimMs),
                                Math.Max(1, fieldClip.TimelineEndMs - Math.Max(0, fieldClip.TimelineStartMs)),
                                ClipAnchor(fieldClip))
                        ])
                ],
                new Dictionary<uint, string>(),
                sameBankModPak,
                aesKey: key,
                wwiserPath: Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                pythonPath: Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"),
                vgmstreamPath: Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM") ?? VgmstreamPath,
                wwiseConsolePath: Environment.GetEnvironmentVariable("HBKWWISE_WWISE_CONSOLE") ?? WwiseConsolePath);

            Assert.Equal(2, sameBank.Compositions.Length);

            const uint scopedMediaId = 1_073_741_801;
            const uint insertedMediaId = 1_073_741_802;
            const uint segmentScopedMediaId = 1_073_741_803;

            Assert.DoesNotContain(index.Media, item => item.Id == scopedMediaId);
            Assert.DoesNotContain(index.Media, item => item.Id == insertedMediaId);
            Assert.DoesNotContain(index.Media, item => item.Id == segmentScopedMediaId);

            var segmentRatio = 136.05 / 164;
            var segmentScoped = await ScopedModPakBuilder.BuildAsync(
                index,
                new ScopedModPakRequest(
                    "Mu_Session_ST01A_Play",
                    timingScope.ObjectId,
                    136.05,
                    136.05,
                    [new ScopedMediaReplacement(6639526, segmentScopedMediaId, replacementWav)],
                    TimelineEdits: originalTimeline.Clips
                        .Where(clip => clip.SourceIdOffset is not null)
                        .GroupBy(clip => clip.SourceIdOffset)
                        .Select(group => group.First())
                        .Select(clip =>
                        {
                            var ratio = clip.SegmentObjectId == knownTimelineClip.SegmentObjectId
                                ? segmentRatio
                                : 1;
                            return new BnkTimelineClipEdit(
                                clip.SourceIdOffset!.Value,
                                Math.Max(0, clip.TimelineStartMs) * ratio,
                                Math.Max(0, clip.BeginTrimMs) * ratio,
                                Math.Max(1, clip.TimelineEndMs - Math.Max(0, clip.TimelineStartMs)) * ratio);
                        })
                        .ToArray(),
                    SegmentTempos:
                    [
                        new ScopedSegmentTempoChange(
                            knownTimelineClip.SegmentObjectId!.Value,
                            136.05,
                            164)
                    ]),
                segmentScopedModPak,
                aesKey: key,
                wwiserPath: Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                pythonPath: Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"),
                vgmstreamPath: Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM") ?? VgmstreamPath,
                wwiseConsolePath: Environment.GetEnvironmentVariable("HBKWWISE_WWISE_CONSOLE") ?? WwiseConsolePath);

            Assert.False(segmentScoped.Validation.HasErrors);
            Assert.Equal(164, Assert.Single(segmentScoped.SegmentTempos!).NewBpm);
            Assert.Contains(segmentScoped.Entries, entry => entry.EndsWith(
                $"/{segmentScopedMediaId}.wem",
                StringComparison.OrdinalIgnoreCase));
            var editedTrackClips = originalTimeline.Clips
                .Where(clip => clip.TrackObjectId == knownTimelineClip.TrackObjectId && clip.SourceIdOffset is not null)
                .GroupBy(clip => clip.SourceIdOffset)
                .Select(group => group.First())
                .OrderBy(clip => clip.PlaylistIndex)
                .ToArray();
            var structuralRatio = 136.05 / 164;
            var insertedTemplate = PlaylistEdit(knownTimelineClip, structuralRatio) with
            {
                MediaId = insertedMediaId,
                StartMs = Math.Max(0, knownTimelineClip.TimelineStartMs) * structuralRatio + 1,
                PreserveAutomation = false
            };
            var scoped = await ScopedModPakBuilder.BuildAsync(
                index,
                new ScopedModPakRequest(
                    "Mu_Session_ST01A_Play",
                    timingScope.ObjectId,
                    136.05,
                    164,
                    [
                        new ScopedMediaReplacement(6639526, scopedMediaId, replacementWav),
                        new ScopedMediaReplacement(6639526, insertedMediaId, replacementWav, ReferencesAlreadyUseNewId: true)
                    ],
                    PlaylistEdits:
                    [
                        new BnkTrackPlaylistEdit(
                            knownTimelineClip.TrackObjectId,
                            editedTrackClips.Select(clip => PlaylistEdit(clip, structuralRatio))
                                .Append(insertedTemplate)
                                .ToArray())
                    ]),
                scopedModPak,
                aesKey: key,
                wwiserPath: Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? WwiserPath,
                pythonPath: Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"),
                vgmstreamPath: Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM") ?? VgmstreamPath,
                wwiseConsolePath: Environment.GetEnvironmentVariable("HBKWWISE_WWISE_CONSOLE") ?? WwiseConsolePath);

            Assert.Equal("Music_ST01", scoped.Bank);
            Assert.Equal(164, scoped.NewBpm);
            Assert.True(scoped.TimelinePatchCount > 0);
            Assert.Equal(3, scoped.Entries.Length);
            Assert.Contains(scoped.Entries, entry => entry.EndsWith($"/{scopedMediaId}.wem", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(scoped.Entries, entry => entry.EndsWith($"/{insertedMediaId}.wem", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(2, scoped.Imports.Length);
            Assert.False(scoped.Validation.HasErrors);
            Assert.Contains(scoped.Validation.Issues, issue => issue.Code == "METER_CLIP_BOUNDS");
            Assert.Equal(scoped.Entries, (await RepakArchive.ListAsync(scopedModPak)).Order(StringComparer.OrdinalIgnoreCase));

            var format = await VgmstreamClient.InspectAsync(
                baseWem,
                Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM") ?? VgmstreamPath);

            Assert.Equal(48_000, format.SampleRate);
            Assert.Equal(2, format.Channels);
            Assert.Equal("Custom Vorbis", format.Encoding);

            var embeddedFormat = await VgmstreamClient.InspectAsync(
                embeddedWem,
                Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM") ?? VgmstreamPath);

            Assert.True(embeddedFormat.DurationSeconds > 0);
        }
        finally
        {
            File.Delete(baseWem);
            File.Delete(exportedStreamedWem);
            File.Delete(updateWem);
            File.Delete(overlaidWem);
            File.Delete(directUpdateWem);
            File.Delete(embeddedWem);
            File.Delete(bnk);
            File.Delete(st06Bnk);
            File.Delete(st06Xml);
            File.Delete(exactOwnerBnk);
            File.Delete(bankXml);
            File.Delete(rewrittenBnk);
            File.Delete(rewrittenXml);
            File.Delete(modPak);
            File.Delete(retimeModPak);
            File.Delete(shortModPak);
            File.Delete(scopedModPak);
            File.Delete(segmentScopedModPak);
            File.Delete(replacementWav);
            File.Delete(retimedBnk);
            File.Delete(retimedXml);
            File.Delete(structuredBnk);
            File.Delete(structuredXml);
            File.Delete(segmentRetimedBnk);
            File.Delete(segmentRetimedXml);
            File.Delete(commonBnk);
            File.Delete(commonXml);
            File.Delete(sameBankModPak);
        }
    }

    private static BnkTrackPlaylistItemEdit PlaylistEdit(BnkTimelineClip clip, double ratio = 1) => new(
        clip.SourceIdOffset,
        clip.MediaId,
        clip.SubTrackId,
        clip.EventId,
        Math.Max(0, clip.TimelineStartMs) * ratio,
        Math.Max(0, clip.BeginTrimMs) * ratio,
        Math.Max(1, clip.TimelineEndMs - Math.Max(0, clip.TimelineStartMs)) * ratio,
        clip.SourceDurationMs,
        OriginalAnchor: ClipAnchor(clip));

    private static BnkTimelineClipAnchor ClipAnchor(BnkTimelineClip clip) => new(
        clip.TrackObjectId,
        clip.SegmentObjectId,
        clip.PlaylistIndex,
        clip.MediaId);
}

public sealed class RealPakFactAttribute : FactAttribute
{
    public RealPakFactAttribute()
    {
        var directory = Environment.GetEnvironmentVariable("HBKWWISE_PAK_DIR") ?? RealPakIntegrationTests.PakDirectory;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HBKWWISE_AES_KEY"))
            || !File.Exists(Environment.GetEnvironmentVariable("HBKWWISE_BASE_PAK") ?? Path.Combine(directory, "Hibiki-WindowsNoEditor.pak"))
            || !File.Exists(Environment.GetEnvironmentVariable("HBKWWISE_UPDATE_PAK")
                ?? Environment.GetEnvironmentVariable("HBKWWISE_DLC_PAK")
                ?? Path.Combine(directory, "Hibiki-WindowsNoEditor_0_P.pak"))
            || !File.Exists(Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM") ?? RealPakIntegrationTests.VgmstreamPath)
            || !File.Exists(Environment.GetEnvironmentVariable("HBKWWISE_WWISER") ?? RealPakIntegrationTests.WwiserPath)
            || !File.Exists(Environment.GetEnvironmentVariable("HBKWWISE_WWISE_CONSOLE") ?? RealPakIntegrationTests.WwiseConsolePath))
        {
            Skip = "Requires HBKWWISE_AES_KEY, vgmstream, wwiser, and the installed base and update PAKs.";
        }
    }
}

internal sealed class DoubleComparer(double epsilon) : IEqualityComparer<double>
{
    public bool Equals(double x, double y) => Math.Abs(x - y) <= epsilon;

    public int GetHashCode(double obj) => 0;
}
