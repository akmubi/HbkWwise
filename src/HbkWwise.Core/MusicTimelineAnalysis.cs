namespace HbkWwise.Core;

public enum MusicClipFitSeverity
{
    Normal,
    Warning,
    Error
}

public sealed record MusicClipFit(
    double TrimmedHeadMs,
    double UsedPhysicalMs,
    double UnusedTailMs,
    double RepeatedMs,
    double SegmentOverrunMs,
    MusicClipFitSeverity Severity)
{
    public bool HasPhysicalDuration => UsedPhysicalMs + UnusedTailMs > 0;
}

public static class MusicTimelineAnalysis
{
    private const double ToleranceMs = 1;

    public static MusicClipFit Analyze(MusicTimelineTrack track, MusicTimelineClip clip)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        var overrun = track.LengthMs is { } length
            ? Math.Max(0, clip.StartMs + clip.DurationMs - length)
            : 0;
        var physical = clip.PhysicalDurationMs.GetValueOrDefault();
        var invalidSourceOffset = physical > 0 && clip.SourceOffsetMs > physical + ToleranceMs;
        var available = physical <= 0 ? 0 : Math.Max(0, physical - clip.SourceOffsetMs);
        var used = Math.Min(clip.DurationMs, available);
        var tail = Math.Max(0, available - clip.DurationMs);
        var repeated = physical <= 0 ? 0 : Math.Max(0, clip.DurationMs - available);
        var severity = overrun > ToleranceMs || invalidSourceOffset
            ? MusicClipFitSeverity.Error
            : clip.SourcePath is not null && repeated > ToleranceMs
                ? MusicClipFitSeverity.Warning
                : MusicClipFitSeverity.Normal;

        return new MusicClipFit(
            Math.Min(clip.SourceOffsetMs, Math.Max(0, physical)),
            used,
            tail,
            repeated,
            overrun,
            severity);
    }
}
