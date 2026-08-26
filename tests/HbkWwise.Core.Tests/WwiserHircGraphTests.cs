using HbkWwise.Core;

namespace HbkWwise.Core.Tests;

public sealed class WwiserHircGraphTests
{
    [Fact]
    public void EventScope_FollowsActionsChildrenAndMedia()
    {
        var graph = WwiserHircGraph.ParseText("""
            <root><object name="HircChunk"><list name="listLoadedItem">
              <object name="CAkEvent"><field name="eHircType" value="4"/><field name="ulID" value="100"/>
                <list name="actions"><field name="ulActionID" value="200"/></list></object>
              <obj na="CAkAction"><fld na="eHircType" va="3"/><fld na="ulID" va="200"/><obj na="target"><fld na="idExt" va="300"/></obj></obj>
              <object name="CAkMusicSegment"><field name="eHircType" value="10"/><field name="ulID" value="300"/><field name="ulChildID" value="301"/>
                <object name="AkBankSourceData"><field offset="4080" name="sourceID" value="500"/><field name="StreamType" value="1"/><field offset="4096" name="uInMemoryMediaSize" value="123"/></object></object>
              <object name="CAkMusicTrack"><field name="eHircType" value="11"/><field name="ulID" value="301"/>
                <object name="AkTrackSrcInfo"><field offset="5000" name="sourceID" value="500"/><field name="fSrcDuration" value="456.5"/></object></object>
            </list></object></root>
            """);

        var result = graph.EventScope("100");

        Assert.Equal([200u], result.ActionIds);
        Assert.Equal([300u], result.TargetIds);
        Assert.Equal([300u, 301u], result.ReachableObjectIds);

        var media = Assert.Single(result.Media);

        Assert.Equal(500u, media.MediaId);
        Assert.Equal(456.5, media.MaxDurationMs);
        Assert.Equal([1], media.StreamTypes);
        Assert.Equal([123], media.MemorySizes);
        Assert.Equal([300u, 301u], media.ObjectIds);
        Assert.Equal([4096], graph.MemorySizeOffsets(500));
        Assert.Equal([4080, 5000], graph.MediaReferenceOffsets(500));
    }

    [Fact]
    public void EventScope_HashesEventNames()
    {
        const string eventName = "Test_Event";
        var eventId = WwiseHash.Fnv1(eventName);
        var graph = WwiserHircGraph.ParseText($"""
            <root><obj na="HircChunk"><lst na="listLoadedItem">
              <obj na="CAkEvent"><fld na="eHircType" va="4"/><fld na="ulID" va="{eventId}"/><fld na="ulActionID" va="2"/></obj>
              <obj na="CAkAction"><fld na="eHircType" va="3"/><fld na="ulID" va="2"/><fld na="idExt" va="3"/></obj>
              <obj na="CAkSound"><fld na="eHircType" va="2"/><fld na="ulID" va="3"/></obj>
            </lst></obj></root>
            """);

        Assert.Equal(eventId, graph.EventScope(eventName).EventId);
    }

    [Fact]
    public void PlayScope_ExcludesStopTargets()
    {
        var graph = WwiserHircGraph.ParseText("""
            <root><object name="HircChunk"><list name="listLoadedItem">
              <object name="CAkEvent"><field name="eHircType" value="4"/><field name="ulID" value="100"/>
                <field name="ulActionID" value="200"/><field name="ulActionID" value="201"/></object>
              <object name="CAkActionPlay"><field name="eHircType" value="3"/><field name="ulID" value="200"/><field name="idExt" value="300"/></object>
              <object name="CAkActionStop"><field name="eHircType" value="3"/><field name="ulID" value="201"/><field name="idExt" value="400"/></object>
              <object name="CAkMusicSegment"><field name="eHircType" value="10"/><field name="ulID" value="300"/></object>
              <object name="CAkMusicSegment"><field name="eHircType" value="10"/><field name="ulID" value="400"/></object>
            </list></object></root>
            """);

        var program = graph.EventProgram("100");

        Assert.Equal([WwiserActionKind.Play, WwiserActionKind.Stop], program.Actions.Select(item => item.Kind));
        Assert.Equal([300u], graph.PlayScope("100").ReachableObjectIds);
        Assert.Equal([300u, 400u], graph.EventScope("100").ReachableObjectIds);
    }

    [Fact]
    public void ContainerFlow_ParsesSwitchPathsAndPlaylistOrder()
    {
        var graph = WwiserHircGraph.ParseText("""
            <root><object name="HircChunk"><list name="listLoadedItem">
              <object name="CAkMusicSwitchCntr"><field name="eHircType" value="12"/><field name="ulID" value="100"/>
                <object name="Arguments"><object name="AkGameSync"><field name="ulGroup" value="1"/></object>
                  <object name="AkGameSync"><field name="ulGroup" value="2"/></object></object>
                <object name="AkDecisionTree"><object name="Node"><field name="key" value="0"/>
                  <object name="Node"><field name="key" value="11"/><object name="Node"><field name="key" value="22"/>
                    <field name="audioNodeId" value="300"/></object></object></object></object>
              </object>
              <object name="CAkMusicRanSeqCntr"><field name="eHircType" value="13"/><field name="ulID" value="200"/>
                <object name="AkMusicRanSeqPlaylistItem"><field name="SegmentID" value="301"/></object>
                <object name="AkMusicRanSeqPlaylistItem"><field name="SegmentID" value="302"/></object>
              </object>
            </list></object></root>
            """);

        Assert.Equal([1u, 2u], graph.Objects[100].FlowArgumentIds);

        var branch = Assert.Single(graph.Objects[100].FlowTargets);

        Assert.Equal(300u, branch.ObjectId);
        Assert.Equal([11u, 22u], branch.Keys);
        Assert.Equal([301u, 302u], graph.Objects[200].FlowTargets.Select(item => item.ObjectId));
    }
}
