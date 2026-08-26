using System.Buffers.Binary;
using HbkWwise.Core;

namespace HbkWwise.Core.Tests;

public sealed class BnkRetimerTests
{
    [Fact]
    public void PlanSegmentOverride_EnablesLocalMeterAndLeavesSiblingScopeUntouched()
    {
        var bank = new byte[512];
        WriteDouble(bank, 100, 110.25358324145535);
        WriteDouble(bank, 108, 0);
        WriteFloat(bank, 116, 136.05f);
        bank[120] = 4;
        bank[121] = 4;
        bank[122] = 1;
        WriteDouble(bank, 200, 1000);
        WriteDouble(bank, 208, 0);
        WriteFloat(bank, 216, 120);
        bank[220] = 4;
        bank[221] = 4;
        bank[222] = 0;
        WriteDouble(bank, 230, 12_000);
        WriteDouble(bank, 300, 4_000);
        var xml = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, """
                <root><object name="HircChunk"><list name="listLoadedItem">
                  <object name="CAkEvent"><field name="eHircType" value="4"/><field name="ulID" value="1"/><field name="ulActionID" value="2"/></object>
                  <object name="CAkActionPlay"><field name="eHircType" value="3"/><field name="ulID" value="2"/><field name="idExt" value="7"/></object>
                  <object name="CAkMusicRanSeqCntr"><field name="eHircType" value="12"/><field name="ulID" value="3"/><field name="DirectParentID" value="0"/><field name="ulChildID" value="7"/>
                    <object name="AkMeterInfo"><field offset="100" name="fGridPeriod" value="110.25358324145535"/><field offset="108" name="fGridOffset" value="0"/><field offset="116" name="fTempo" value="136.05"/><field offset="120" name="uTimeSigNumBeatsBar" value="4"/><field offset="121" name="uTimeSigBeatValue" value="4"/></object><field offset="122" name="bMeterInfoFlag" value="1"/></object>
                  <object name="CAkMusicSegment"><field name="eHircType" value="10"/><field name="ulID" value="7"/><field name="DirectParentID" value="3"/><field name="ulChildID" value="8"/>
                    <object name="AkMeterInfo"><field offset="200" name="fGridPeriod" value="1000"/><field offset="208" name="fGridOffset" value="0"/><field offset="216" name="fTempo" value="120"/><field offset="220" name="uTimeSigNumBeatsBar" value="4"/><field offset="221" name="uTimeSigBeatValue" value="4"/></object><field offset="222" name="bMeterInfoFlag" value="0"/>
                    <object name="MusicSegmentInitialValues"><field offset="230" name="fDuration" value="12000"/></object></object>
                  <object name="CAkMusicTrack"><field name="eHircType" value="11"/><field name="ulID" value="8"/><field name="DirectParentID" value="7"/>
                    <object name="AkTrackSrcInfo"><field name="sourceID" value="99"/><field offset="300" name="fPlayAt" value="4000"/></object></object>
                </list></object></root>
                """);

            var inheritedScope = Assert.Single(BnkRetimer.FindTimingScopes(xml, "1"));

            Assert.Equal(3u, inheritedScope.ObjectId);
            Assert.Equal([3u, 7u, 8u], inheritedScope.ObjectIds);

            var plan = BnkRetimer.PlanSegmentOverride(bank, xml, 3, 7, 164, 136.05, eventNameOrId: "1");
            var ratio = (double)136.05f / 164;

            Assert.Equal([7u, 8u], plan.RetimeObjectIds);
            Assert.Contains(plan.Patches, patch => patch.Kind == "meter-enable" && patch.Offset == 222);
            Assert.Contains(plan.Patches, patch => patch.Kind == "track-play" && patch.Offset == 300);
            Assert.DoesNotContain(plan.Patches, patch => patch.Offset is 100 or 108 or 116 or 122);

            var result = BnkRetimer.Apply(bank, plan);

            Assert.Equal(1, result[222]);
            Assert.Equal(164, BinaryPrimitives.ReadSingleLittleEndian(result.AsSpan(216)), 3);
            Assert.Equal(12_000 * ratio, BinaryPrimitives.ReadDoubleLittleEndian(result.AsSpan(230)), 6);
            Assert.Equal(4_000 * ratio, BinaryPrimitives.ReadDoubleLittleEndian(result.AsSpan(300)), 6);
        }
        finally
        {
            File.Delete(xml);
        }
    }

    [Fact]
    public void Plan_RetimesOneExplicitScopeAndStopsAtNestedMeter()
    {
        var bank = new byte[512];
        WriteDouble(bank, 100, 441.0143329658214);
        WriteDouble(bank, 108, 0);
        WriteFloat(bank, 116, 136.05f);
        WriteDouble(bank, 120, 120_000);
        WriteDouble(bank, 128, 0);
        WriteDouble(bank, 136, 120_000);
        WriteDouble(bank, 200, 95_259.0959206174);
        WriteDouble(bank, 208, 1_000);
        WriteDouble(bank, 216, -2_000);
        WriteDouble(bank, 300, 12_000);
        WriteDouble(bank, 360, 42_000);
        WriteDouble(bank, 320, 441.0143329658214);
        WriteDouble(bank, 328, 0);
        WriteFloat(bank, 336, 136.05f);
        var xml = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xml, """
                <root><object name="HircChunk"><list name="listLoadedItem">
                  <object name="CAkEvent"><field name="eHircType" value="4"/><field name="ulID" value="1"/><field name="ulActionID" value="2"/></object>
                  <object name="CAkAction"><field name="eHircType" value="3"/><field name="ulID" value="2"/><field name="idExt" value="3"/></object>
                  <object name="CAkMusicSegment"><field name="eHircType" value="10"/><field name="ulID" value="3"/><field name="ulChildID" value="4"/><field name="ulChildID" value="5"/><field name="ulChildID" value="6"/>
                    <object name="AkMeterInfo"><field offset="100" name="fGridPeriod" value="441.0143329658214"/><field offset="108" name="fGridOffset" value="0"/><field offset="116" name="fTempo" value="136.05"/></object><field name="bMeterInfoFlag" value="1"/>
                    <object name="MusicSegmentInitialValues"><field offset="120" name="fDuration" value="120000"/><list name="pArrayMarkers">
                      <object name="AkMusicMarkerWwise"><field name="id" value="43573010"/><field offset="128" name="fPosition" value="0"/></object>
                      <object name="AkMusicMarkerWwise"><field name="id" value="1539036744"/><field offset="136" name="fPosition" value="120000"/></object>
                    </list></object>
                    <object name="AkMusicTransitionRule"><object name="AkMusicTransSrcRule"><field name="eSyncType" value="7"/><field name="uCueFilterHash" value="999"/></object><object name="AkMusicTransDstRule"><field name="uJumpToID" value="888"/></object></object>
                    <object name="AkMusicRanSeqPlaylistItem"><field name="SegmentID" value="3"/><field name="Loop" value="0"/><field name="LoopMin" value="0"/><field name="LoopMax" value="0"/></object></object>
                  <object name="CAkMusicTrack"><field name="eHircType" value="11"/><field name="ulID" value="4"/>
                    <object name="AkTrackSrcInfo"><field name="sourceID" value="99"/><field offset="200" name="fPlayAt" value="95259.0959206174"/><field offset="208" name="fBeginTrimOffset" value="1000"/><field offset="216" name="fEndTrimOffset" value="-2000"/><field name="fSrcDuration" value="100000"/></object></object>
                  <object name="CAkMusicTrack"><field name="eHircType" value="11"/><field name="ulID" value="5"/>
                    <object name="AkMeterInfo"><field offset="320" name="fGridPeriod" value="441.0143329658214"/><field offset="328" name="fGridOffset" value="0"/><field offset="336" name="fTempo" value="136.05"/></object><field name="bMeterInfoFlag" value="1"/>
                    <object name="AkTrackSrcInfo"><field name="sourceID" value="100"/><field offset="300" name="fPlayAt" value="12000"/></object></object>
                  <object name="CAkMusicTrack"><field name="eHircType" value="11"/><field name="ulID" value="6"/>
                    <object name="AkTrackSrcInfo"><field name="sourceID" value="101"/><field offset="360" name="fPlayAt" value="42000"/><field name="fSrcDuration" value="50000"/></object></object>
                </list></object></root>
                """);

            var scopes = BnkRetimer.FindTimingScopes(xml, "1");

            Assert.Collection(
                scopes,
                scope =>
                {

                    Assert.Equal(3u, scope.ObjectId);
                    Assert.Equal([136.05], scope.Bpms, new DoubleComparer(0.001));
                    Assert.Equal(3, scope.RetimeObjects);
                    Assert.Equal([99u, 101u], scope.MediaIds);
                },
                scope =>
                {

                    Assert.Equal(5u, scope.ObjectId);
                    Assert.Equal([136.05], scope.Bpms, new DoubleComparer(0.001));
                    Assert.Equal([100u], scope.MediaIds);
                });

            var plan = BnkRetimer.Plan(bank, xml, 3, 164, 136.05, eventNameOrId: "1");

            Assert.Equal(3u, plan.ScopeObjectId);
            Assert.Equal([99u, 101u], plan.AffectedMediaIds);
            Assert.Equal([3u, 4u, 6u], plan.RetimeObjectIds);
            Assert.Contains(plan.Patches, patch => patch.Kind == "track-play" && patch.Offset == 200);
            Assert.Contains(plan.Patches, patch => patch.Kind == "track-play" && patch.Offset == 360);
            Assert.DoesNotContain(plan.Patches, patch => patch.Offset == 300);

            var result = BnkRetimer.Apply(bank, plan);

            Assert.Equal(95_259.0959206174 * (double)136.05f / 164, BinaryPrimitives.ReadDoubleLittleEndian(result.AsSpan(200)), 6);
            Assert.Equal(12_000, BinaryPrimitives.ReadDoubleLittleEndian(result.AsSpan(300)));
            Assert.Equal(42_000 * (double)136.05f / 164, BinaryPrimitives.ReadDoubleLittleEndian(result.AsSpan(360)), 6);

            var nestedPlan = BnkRetimer.Plan(bank, xml, 5, 164, 136.05, eventNameOrId: "1");

            Assert.Equal([5u], nestedPlan.RetimeObjectIds);
            Assert.Contains(nestedPlan.Patches, patch => patch.Offset == 300);
            Assert.DoesNotContain(nestedPlan.Patches, patch => patch.Offset is 200 or 360);
            Assert.Throws<InvalidDataException>(() =>
                BnkRetimer.Plan(bank, xml, 5, 164, 120, eventNameOrId: "1"));

            var validation = BnkDurationValidator.Validate(
                xml,
                3,
                new Dictionary<uint, double> { [99] = 90_000, [101] = 60_000, [123] = 1_000 },
                eventNameOrId: "1");

            Assert.True(validation.HasErrors);
            Assert.Equal(BnkDurationFit.TooShort, Assert.Single(validation.Checks, item => item.MediaId == 99).Fit);
            Assert.Equal(BnkDurationFit.Longer, Assert.Single(validation.Checks, item => item.MediaId == 101).Fit);
            Assert.Equal(BnkDurationFit.NotUsed, Assert.Single(validation.Checks, item => item.MediaId == 123).Fit);

            var usage = Assert.Single(validation.ClipUsages, item => item.MediaId == 99);

            Assert.Equal(1_000, usage.BeginTrimMs);
            Assert.Equal(-2_000, usage.EndTrimMs);
            Assert.Equal(
                BnkDurationFit.Match,

                Assert.Single(BnkDurationValidator.Validate(
                    xml,
                    3,
                    new Dictionary<uint, double> { [99] = 100_000.5 }).Checks).Fit);

            var timeline = BnkTimelineValidator.Validate(
                xml,
                3,
                new Dictionary<uint, double> { [99] = 100_000, [101] = 50_000 },
                136.05,
                164,
                eventNameOrId: "1");

            Assert.Single(timeline.Segments);
            Assert.Equal(2, timeline.Clips.Length);
            Assert.Single(timeline.Loops);
            Assert.Contains(timeline.Issues, item => item.Code == "CLIP_BOUNDS" && item.MediaId == 99);
            Assert.Contains(timeline.Issues, item => item.Code == "TRANSITION_SOURCE_CUE");
            Assert.Contains(timeline.Issues, item => item.Code == "TRANSITION_DESTINATION_CUE");

            var editor = new MusicTimelineDocument();
            var imported = MusicTimelineImporter.LoadSegment(
                editor,
                timeline,
                timeline.Segments[0].ObjectId,
                164,
                new Dictionary<uint, string> { [99] = "Music/Test.wav", [101] = "Music/Other.wav" });

            Assert.Equal(2, imported.Clips);
            Assert.Equal(164, editor.Bpm);
            Assert.Equal("Test", editor.Tracks.SelectMany(track => track.Clips).Single(clip => clip.MediaId == 99).Name);
            Assert.Contains(editor.Markers, marker => marker.Name == "Entry");
            Assert.Contains(editor.Markers, marker => marker.Name == "Exit");

            var fasterEditor = new MusicTimelineDocument();
            MusicTimelineImporter.LoadSegment(
                fasterEditor,
                timeline,
                timeline.Segments[0].ObjectId,
                328,
                timeRatio: 0.5);

            Assert.Equal(editor.TimelineLengthMs * 0.5, fasterEditor.TimelineLengthMs);
            Assert.Equal(editor.Markers[0].PositionMs * 0.5, fasterEditor.Markers[0].PositionMs);
        }
        finally
        {
            File.Delete(xml);
        }
    }

    private static void WriteDouble(byte[] data, int offset, double value) =>
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(offset), value);

    private static void WriteFloat(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);

    private sealed class DoubleComparer(double epsilon) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= epsilon;

        public int GetHashCode(double obj) => 0;
    }
}
