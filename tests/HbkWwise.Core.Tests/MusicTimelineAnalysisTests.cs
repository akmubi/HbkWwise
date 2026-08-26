namespace HbkWwise.Core.Tests;

public sealed class MusicTimelineAnalysisTests
{
    [Fact]
    public void Analyze_SeparatesTrimmedHeadUsedAudioAndUnusedTail()
    {
        var clip = Clip(start: 1_000, sourceOffset: 2_000, duration: 5_000, physical: 10_000);
        var fit = MusicTimelineAnalysis.Analyze(Track(clip, 20_000), clip);

        Assert.Equal(2_000, fit.TrimmedHeadMs);
        Assert.Equal(5_000, fit.UsedPhysicalMs);
        Assert.Equal(3_000, fit.UnusedTailMs);
        Assert.Equal(0, fit.RepeatedMs);
        Assert.Equal(MusicClipFitSeverity.Normal, fit.Severity);
    }

    [Fact]
    public void Analyze_WarnsWhenReplacementMustRepeatAndErrorsOnSegmentOverrun()
    {
        var source = Path.Combine(Path.GetTempPath(), "replacement.wav");
        var looping = Clip(0, 1_000, 6_000, 4_000) with { SourcePath = source };
        var warning = MusicTimelineAnalysis.Analyze(Track(looping, 10_000), looping);

        Assert.Equal(3_000, warning.RepeatedMs);
        Assert.Equal(MusicClipFitSeverity.Warning, warning.Severity);

        var overrun = looping with { StartMs = 7_000 };
        var error = MusicTimelineAnalysis.Analyze(Track(overrun, 10_000), overrun);

        Assert.Equal(3_000, error.SegmentOverrunMs);
        Assert.Equal(MusicClipFitSeverity.Error, error.Severity);
    }

    private static MusicTimelineClip Clip(double start, double sourceOffset, double duration, double physical) =>
        new(Guid.NewGuid(), 1, "Clip", null, start, sourceOffset, duration, PhysicalDurationMs: physical);

    private static MusicTimelineTrack Track(MusicTimelineClip clip, double length) =>
        new(Guid.NewGuid(), "Track", [clip], LengthMs: length);
}
