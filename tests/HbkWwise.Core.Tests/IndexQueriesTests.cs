using HbkWwise.Core;

namespace HbkWwise.Core.Tests;

public sealed class IndexQueriesTests
{
    [Fact]
    public void MediaRecord_DistinguishesWwiseMidiFromPlayableAudio()
    {
        var midi = new MediaRecord(1, "Music", "Music\\Tempo.mid", "SFX\\Music\\Tempo.wmid", "SFX",
            false, true, null, []);
        var audio = midi with { Id = 2, SourceName = "Music\\Song.wav", Path = "SFX\\Music\\Song.wem" };

        Assert.True(midi.IsWwiseMidi);
        Assert.False(midi.IsPlayableAudio);
        Assert.False(audio.IsWwiseMidi);
        Assert.True(audio.IsPlayableAudio);
    }

    [Fact]
    public void Overrides_DeduplicatesSharedMediaAndReportsEffectivePak()
    {
        var assets = new[]
        {
            new PakAsset("base.pak", "Wwise/1.wem", 0, false),
            new PakAsset("update.pak", "Wwise/1.wem", 1, true)
        };
        var media = new MediaRecord(1, "A", "Music\\Song.wav", "", "SFX", true, false, null, [], assets);
        var duplicateBankReference = media with { Bank = "B" };
        var index = new WwiseIndex(DateTimeOffset.UtcNow, [], [], [], [media, duplicateBankReference], []);

        var item = Assert.Single(index.Overrides());

        Assert.Equal("media", item.Kind);
        Assert.Equal("update.pak", Assert.Single(item.Assets, asset => asset.IsEffective).PakPath);
        Assert.Equal(2, index.Search("song", new SearchOptions(Pak: "update")).Count);
        Assert.Empty(index.Search("song", new SearchOptions(Pak: "base")));
    }
}
