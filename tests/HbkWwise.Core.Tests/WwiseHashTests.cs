using HbkWwise.Core;

namespace HbkWwise.Core.Tests;

public sealed class WwiseHashTests
{
    [Fact]
    public void Fnv1_MatchesKnownHiFiRushEvent()
    {

        Assert.Equal(4285653941u, WwiseHash.Fnv1("Mu_Session_ST01A_Stop"));
    }

    [Fact]
    public void Fnv1_IsCaseInsensitive()
    {

        Assert.Equal(WwiseHash.Fnv1("GP_Mu_Sync_Rate_01"), WwiseHash.Fnv1("gp_mu_sync_rate_01"));
    }

    [Fact]
    public void MediaId_UsesTheGameMediaIdDomain()
    {
        var id = WwiseHash.MediaId("HBK_473417611_913141848_source.wav_0");

        Assert.InRange(id, 1u, WwiseHash.MaxMediaId);
        Assert.Equal(WwiseHash.Fnv1("HBK_473417611_913141848_source.wav_0") & WwiseHash.MaxMediaId, id);
    }

    [Fact]
    public void AllocateMediaId_SkipsCollision()
    {
        const string seed = "HBK_1_2_song.wav";
        var first = WwiseHash.MediaId($"{seed}_0");

        var allocated = WwiseHash.AllocateMediaId(seed, new HashSet<uint> { first });

        Assert.Equal(WwiseHash.MediaId($"{seed}_1"), allocated);
    }
}
