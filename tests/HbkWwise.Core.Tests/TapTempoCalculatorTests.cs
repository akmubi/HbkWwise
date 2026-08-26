namespace HbkWwise.Core.Tests;

public sealed class TapTempoCalculatorTests
{
    [Fact]
    public void EstimateBpm_UsesTapIntervals()
    {
        var bpm = TapTempoCalculator.EstimateBpm([0, 500, 1_000, 1_500]);

        Assert.Equal(120, bpm);
    }

    [Fact]
    public void EstimateBpm_IgnoresOneMissedBeat()
    {
        var bpm = TapTempoCalculator.EstimateBpm([0, 500, 1_000, 2_000, 2_500, 3_000]);

        Assert.Equal(120, bpm);
    }

    [Fact]
    public void Fit_RecoversBeatPhaseAndLeadingSilenceAtCorrectedBpm()
    {
        var alignment = BeatGridAlignmentCalculator.Fit([100, 700, 1_300, 1_900], 100);

        Assert.NotNull(alignment);
        Assert.Equal(100, alignment.PhaseMs, 6);
        Assert.Equal(500, alignment.LeadingSilenceMs, 6);
        Assert.Equal(0, alignment.MeanTapErrorMs, 6);
    }

    [Fact]
    public void Fit_UsesCircularMeanAcrossBeatBoundary()
    {
        var alignment = BeatGridAlignmentCalculator.Fit([598, 1_202, 1_799, 2_401], 100);

        Assert.NotNull(alignment);
        Assert.True(alignment.PhaseMs < 3 || alignment.PhaseMs > 597);
        Assert.InRange(alignment.MeanTapErrorMs, 0, 3);
    }

    [Fact]
    public void SnapAudioOffset_UsesTapMarkersAndOnlySnapsWithinTolerance()
    {
        var snapped = BeatGridAlignmentCalculator.SnapAudioOffset(
            [100, 700, 1_300], 100, 485, 100);
        var unsnapped = BeatGridAlignmentCalculator.SnapAudioOffset(
            [100, 700, 1_300], 100, 350, 100);

        Assert.Equal(500, snapped, 6);
        Assert.Equal(350, unsnapped, 6);
    }

    [Fact]
    public void SnapAudioOffset_UsesAudioStartBeforeAnyTapsExist()
    {
        var snapped = BeatGridAlignmentCalculator.SnapAudioOffset([], 100, 590, 100);

        Assert.Equal(600, snapped, 6);
    }
}
