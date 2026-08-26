using System.Buffers.Binary;

namespace HbkWwise.Core.Tests;

public sealed class BnkTimelineStructureEditorTests
{
    [Fact]
    public void Apply_RebuildsPlaylistAndMovesAutomationToRetainedSource()
    {
        var (bank, xml) = Fixture();
        try
        {
            var output = BnkTimelineStructureEditor.Apply(bank, xml,
            [
                new BnkTrackPlaylistEdit(123,
                [
                    new BnkTrackPlaylistItemEdit(73, 222, 0, 0, 200, 20, 300, 400),
                    new BnkTrackPlaylistItemEdit(29, 111, 0, 0, 0, 0, 100, 100),
                    new BnkTrackPlaylistItemEdit(29, 111, 0, 0, 100, 0, 100, 100)
                ])
            ]);

            Assert.Equal(bank.Length + 44, output.Data.Length);
            Assert.Equal(196u, ReadUInt32(output.Data, 4));
            Assert.Equal(187u, ReadUInt32(output.Data, 13));
            Assert.Equal(3u, ReadUInt32(output.Data, 21));
            Assert.Equal(222u, ReadUInt32(output.Data, 29));
            Assert.Equal(180, ReadDouble(output.Data, 37));
            Assert.Equal(111u, ReadUInt32(output.Data, 73));
            Assert.Equal(111u, ReadUInt32(output.Data, 117));
            Assert.Equal(1u, ReadUInt32(output.Data, 161));
            Assert.Equal(1u, ReadUInt32(output.Data, 165));
            Assert.Equal(1, output.AddedClips);
            Assert.Equal(1, output.MovedAutomations);
        }
        finally
        {
            File.Delete(xml);
        }
    }

    [Fact]
    public void Apply_CanEmptyAnAuthoredTrackAndRemovesItsAutomation()
    {
        var (bank, xml) = Fixture();
        try
        {
            var output = BnkTimelineStructureEditor.Apply(
                bank,
                xml,
                [new BnkTrackPlaylistEdit(123, [])]);

            Assert.Equal(bank.Length - 124, output.Data.Length);
            Assert.Equal(28u, ReadUInt32(output.Data, 4));
            Assert.Equal(19u, ReadUInt32(output.Data, 13));
            Assert.Equal(0u, ReadUInt32(output.Data, 21));
            Assert.Equal(1u, ReadUInt32(output.Data, 25));
            Assert.Equal(0u, ReadUInt32(output.Data, 29));
            Assert.Equal(2, output.RemovedClips);
            Assert.Equal(0, output.MovedAutomations);
        }
        finally
        {
            File.Delete(xml);
        }
    }

    [Fact]
    public void Apply_ReplacesAuthoredFadesWithExplicitGraphs()
    {
        var (bank, xml) = Fixture();
        try
        {
            var output = BnkTimelineStructureEditor.Apply(bank, xml,
            [
                new BnkTrackPlaylistEdit(123,
                [
                    new BnkTrackPlaylistItemEdit(29, 111, 0, 0, 0, 0, 100, 100,
                        Fades: new BnkClipFadeEdit(20, 30)),
                    new BnkTrackPlaylistItemEdit(73, 222, 0, 0, 100, 0, 200, 200)
                ])
            ]);

            Assert.Equal(bank.Length + 48, output.Data.Length);
            Assert.Equal(2u, ReadUInt32(output.Data, 117));
            Assert.Equal(3u, ReadUInt32(output.Data, 125));
            Assert.Equal(0.02f, ReadFloat(output.Data, 145), 4);
            Assert.Equal(4u, ReadUInt32(output.Data, 161));
            Assert.Equal(0.07f, ReadFloat(output.Data, 181), 4);
            Assert.Equal(0.1f, ReadFloat(output.Data, 193), 4);
            Assert.Equal(2, output.MovedAutomations);
        }
        finally
        {
            File.Delete(xml);
        }
    }

    [Fact]
    public void Apply_AddsAPlayableSourceDefinitionUsingExplicitTemplateMedia()
    {
        var bank = new byte[99];
        "HIRC"u8.CopyTo(bank);
        WriteUInt32(bank, 4, 91);
        WriteUInt32(bank, 8, 1);
        bank[12] = 11;
        WriteUInt32(bank, 13, 82);
        WriteUInt32(bank, 17, 123);
        bank[21] = 0;
        WriteUInt32(bank, 22, 1);
        WriteUInt32(bank, 26, 262145);
        bank[30] = 1;
        WriteUInt32(bank, 31, 111);
        WriteUInt32(bank, 35, 555);
        bank[39] = 0;
        WriteUInt32(bank, 40, 1);
        WriteItem(bank, 44, 111, 0, 0, 100, 100);
        WriteUInt32(bank, 88, 1);
        WriteUInt32(bank, 92, 0);
        bank[96] = 0xAA;
        bank[97] = 0xBB;
        bank[98] = 0xCC;
        var xml = Path.Combine(Path.GetTempPath(), $"hbk-track-source-{Guid.NewGuid():N}.xml");
        File.WriteAllText(xml, """
            <root>
              <object name="CAkMusicTrack">
                <field offset="12" type="u8" name="eHircType" value="11"/>
                <field offset="13" type="u32" name="dwSectionSize" value="82"/>
                <field offset="17" type="sid" name="ulID" value="123"/>
                <field offset="21" type="u8" name="uFlags" value="0"/>
                <field offset="22" type="u32" name="numSources" value="1"/>
                <list name="pSource" count="1">
                  <object name="AkBankSourceData">
                    <field offset="26" type="u32" name="ulPluginID" value="262145"/>
                    <field offset="30" type="u8" name="StreamType" value="1"/>
                    <object name="AkMediaInformation">
                      <field offset="31" type="tid" name="sourceID" value="111"/>
                      <field offset="35" type="u32" name="uInMemoryMediaSize" value="555"/>
                      <field offset="39" type="u8" name="uSourceBits" value="0"/>
                    </object>
                  </object>
                </list>
                <field offset="40" type="u32" name="numPlaylistItem" value="1"/>
                <list name="pPlaylist" count="1">
                  <object name="AkTrackSrcInfo">
                    <field offset="44" type="u32" name="trackID" value="0"/>
                    <field offset="48" type="tid" name="sourceID" value="111"/>
                    <field offset="52" type="tid" name="eventID" value="0"/>
                    <field offset="56" type="d64" name="fPlayAt" value="0"/>
                    <field offset="64" type="d64" name="fBeginTrimOffset" value="0"/>
                    <field offset="72" type="d64" name="fEndTrimOffset" value="0"/>
                    <field offset="80" type="d64" name="fSrcDuration" value="100"/>
                  </object>
                </list>
                <field offset="88" type="u32" name="numSubTrack" value="1"/>
                <field offset="92" type="u32" name="numClipAutomationItem" value="0"/>
                <list name="pItems" count="0"/>
              </object>
            </root>
            """);
        try
        {
            var output = BnkTimelineStructureEditor.Apply(bank, xml,
            [
                new BnkTrackPlaylistEdit(123,
                [
                    new BnkTrackPlaylistItemEdit(
                        999,
                        222,
                        0,
                        0,
                        0,
                        0,
                        100,
                        100,
                        TemplateMediaId: 111)
                ])
            ]);

            Assert.Equal(bank.Length + 14, output.Data.Length);
            Assert.Equal(2u, ReadUInt32(output.Data, 22));
            Assert.Equal(111u, ReadUInt32(output.Data, 31));
            Assert.Equal(222u, ReadUInt32(output.Data, 45));
            Assert.Equal(555u, ReadUInt32(output.Data, 49));
            Assert.Equal(1u, ReadUInt32(output.Data, 54));
            Assert.Equal(222u, ReadUInt32(output.Data, 62));
        }
        finally
        {
            File.Delete(xml);
        }
    }

    private static (byte[] Bank, string Xml) Fixture()
    {
        var bank = new byte[160];
        "HIRC"u8.CopyTo(bank);
        WriteUInt32(bank, 4, 152);
        WriteUInt32(bank, 8, 1);
        bank[12] = 11;
        WriteUInt32(bank, 13, 143);
        WriteUInt32(bank, 17, 123);
        WriteUInt32(bank, 21, 2);
        WriteItem(bank, 25, 111, 0, 0, 100, 100);
        WriteItem(bank, 69, 222, 100, 0, 200, 200);
        WriteUInt32(bank, 113, 1);
        WriteUInt32(bank, 117, 1);
        WriteUInt32(bank, 121, 0);
        WriteUInt32(bank, 125, 4);
        WriteUInt32(bank, 129, 2);
        WriteFloat(bank, 133, 0);
        WriteFloat(bank, 137, 1);
        WriteUInt32(bank, 141, 9);
        WriteFloat(bank, 145, 1);
        WriteFloat(bank, 149, 0);
        WriteUInt32(bank, 153, 9);
        bank[157] = 0xAA;
        bank[158] = 0xBB;
        bank[159] = 0xCC;

        var path = Path.Combine(Path.GetTempPath(), $"hbk-track-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, """
            <root>
              <object name="CAkMusicTrack">
                <field offset="12" type="u8" name="eHircType" value="11"/>
                <field offset="13" type="u32" name="dwSectionSize" value="143"/>
                <field offset="17" type="sid" name="ulID" value="123"/>
                <object name="MusicTrackInitialValues">
                  <field offset="21" type="u32" name="numPlaylistItem" value="2"/>
                  <list name="pPlaylist" count="2">
                    <object name="AkTrackSrcInfo" index="0">
                      <field offset="25" type="u32" name="trackID" value="0"/>
                      <field offset="29" type="tid" name="sourceID" value="111"/>
                      <field offset="33" type="tid" name="eventID" value="0"/>
                      <field offset="37" type="d64" name="fPlayAt" value="0"/>
                      <field offset="45" type="d64" name="fBeginTrimOffset" value="0"/>
                      <field offset="53" type="d64" name="fEndTrimOffset" value="0"/>
                      <field offset="61" type="d64" name="fSrcDuration" value="100"/>
                    </object>
                    <object name="AkTrackSrcInfo" index="1">
                      <field offset="69" type="u32" name="trackID" value="0"/>
                      <field offset="73" type="tid" name="sourceID" value="222"/>
                      <field offset="77" type="tid" name="eventID" value="0"/>
                      <field offset="81" type="d64" name="fPlayAt" value="100"/>
                      <field offset="89" type="d64" name="fBeginTrimOffset" value="0"/>
                      <field offset="97" type="d64" name="fEndTrimOffset" value="0"/>
                      <field offset="105" type="d64" name="fSrcDuration" value="200"/>
                    </object>
                  </list>
                  <field offset="113" type="u32" name="numSubTrack" value="1"/>
                  <field offset="117" type="u32" name="numClipAutomationItem" value="1"/>
                  <list name="pItems" count="1">
                    <object name="AkClipAutomation" index="0">
                      <field offset="121" type="u32" name="uClipIndex" value="0"/>
                      <field offset="125" type="u32" name="eAutoType" value="4"/>
                      <field offset="129" type="u32" name="uNumPoints" value="2"/>
                      <list name="pArrayGraphPoints" count="2">
                        <object name="AkRTPCGraphPoint" index="0">
                          <field offset="133" type="f32" name="From" value="0"/>
                          <field offset="137" type="f32" name="To" value="1"/>
                          <field offset="141" type="u32" name="Interp" value="9"/>
                        </object>
                        <object name="AkRTPCGraphPoint" index="1">
                          <field offset="145" type="f32" name="From" value="1"/>
                          <field offset="149" type="f32" name="To" value="0"/>
                          <field offset="153" type="u32" name="Interp" value="9"/>
                        </object>
                      </list>
                    </object>
                  </list>
                </object>
              </object>
            </root>
            """);
        return (bank, path);
    }

    private static void WriteItem(byte[] data, int offset, uint mediaId, double start, double sourceOffset, double duration, double sourceDuration)
    {
        WriteUInt32(data, offset, 0);
        WriteUInt32(data, offset + 4, mediaId);
        WriteUInt32(data, offset + 8, 0);
        WriteDouble(data, offset + 12, start - sourceOffset);
        WriteDouble(data, offset + 20, sourceOffset);
        WriteDouble(data, offset + 28, duration - (sourceDuration - sourceOffset));
        WriteDouble(data, offset + 36, sourceDuration);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static void WriteDouble(byte[] data, int offset, double value) =>
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(offset, 8), value);

    private static double ReadDouble(byte[] data, int offset) =>
        BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(offset, 8));

    private static float ReadFloat(byte[] data, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));

    private static void WriteFloat(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
}
