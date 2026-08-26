using System.Globalization;

namespace HbkWwise.Core;

public sealed record MusicTimelineImportResult(
    uint SegmentObjectId,
    int Tracks,
    int Clips,
    int Markers);

public sealed record MusicScopeTimelineImportResult(
    uint ScopeObjectId,
    int Segments,
    int Tracks,
    int Clips,
    int Markers,
    int Media);

public static class MusicTimelineImporter
{
    private const uint EntryMarkerId = 43573010;
    private const uint ExitMarkerId = 1539036744;

    public static MusicTimelineImportResult LoadSegment(
        MusicTimelineDocument document,
        BnkTimelineValidation validation,
        uint segmentObjectId,
        double bpm,
        IReadOnlyDictionary<uint, string>? mediaNames = null,
        bool snapEnabled = true,
        double timeRatio = 1)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(validation);
        if (!double.IsFinite(timeRatio) || timeRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeRatio));
        }

        var segment = validation.Segments.SingleOrDefault(item => item.ObjectId == segmentObjectId)
            ?? throw new KeyNotFoundException($"Music segment {segmentObjectId} was not found in scope {validation.ScopeObjectId}.");
        var clips = validation.Clips.Where(clip => clip.SegmentObjectId == segment.ObjectId).ToArray();
        var trackIds = segment.TrackObjectIds.Concat(clips.Select(clip => clip.TrackObjectId)).Distinct().ToArray();
        var tracks = trackIds.Select(trackId => new MusicTimelineTrack(
            Guid.NewGuid(),
            $"Track {trackId}",
            clips.Where(clip => clip.TrackObjectId == trackId)
                .Select(clip =>
                {
                    var name = mediaNames?.GetValueOrDefault(clip.MediaId);
                    var start = Math.Max(0, clip.TimelineStartMs) * timeRatio;

                    return new MusicTimelineClip(
                        Guid.NewGuid(),
                        clip.MediaId,
                        name is null ? clip.MediaId.ToString() : Path.GetFileNameWithoutExtension(name),
                        null,
                        start,
                        Math.Max(0, clip.BeginTrimMs) * timeRatio,
                        Math.Max(1, (clip.TimelineEndMs - Math.Max(0, clip.TimelineStartMs)) * timeRatio),
                        clip.SourceIdOffset,
                        FieldOffsets: clip.FieldOffsets,
                        PhysicalDurationMs: clip.SourceDurationMs,
                        PlaylistIndex: clip.PlaylistIndex,
                        HasFadeIn: clip.HasFadeIn,
                        HasFadeOut: clip.HasFadeOut,
                        RepeatsSource: clip.RepeatsSource,
                        FadeInMs: clip.FadeInMs,
                        FadeOutMs: clip.FadeOutMs);
                }).ToArray(),
            trackId,
            segment.ObjectId,
            segment.DurationMs * timeRatio)).ToArray();

        if (tracks.Length == 0)
        {
            tracks = [new MusicTimelineTrack(Guid.NewGuid(), "Empty segment", [])];
        }

        var markers = segment.Markers.Select(marker => new MusicTimelineMarker(
            marker.Id,
            marker.Id switch
            {
                EntryMarkerId => "Entry",
                ExitMarkerId => "Exit",
                _ => $"Cue {marker.Id}"
            },
            marker.PositionMs * timeRatio,
            segment.ObjectId,
            marker.PositionOffset is { } offset ? [offset] : null)).ToArray();
        document.Reset(bpm, segment.DurationMs * timeRatio, tracks, markers, snapEnabled: snapEnabled);

        return new MusicTimelineImportResult(segment.ObjectId, tracks.Length, clips.Length, markers.Length);
    }

    public static MusicScopeTimelineImportResult LoadScope(
        MusicTimelineDocument document,
        BnkTimelineValidation validation,
        double bpm,
        IReadOnlyDictionary<uint, string>? mediaNames = null,
        bool snapEnabled = true,
        double timeRatio = 1,
        uint? selectedSegmentId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(validation);
        if (!double.IsFinite(timeRatio) || timeRatio <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeRatio));
        }

        var segments = validation.Segments
            .OrderByDescending(segment => segment.ObjectId == selectedSegmentId)
            .ThenBy(segment => SegmentLabel(segment, validation.Clips, mediaNames), StringComparer.OrdinalIgnoreCase)
            .ThenBy(segment => segment.ObjectId)
            .ToArray();
        var tracks = segments.SelectMany(segment =>
        {
            var clips = validation.Clips.Where(clip => clip.SegmentObjectId == segment.ObjectId).ToArray();
            var label = SegmentLabel(segment, clips, mediaNames);

            return segment.TrackObjectIds.Concat(clips.Select(clip => clip.TrackObjectId)).Distinct()
                .Select(trackId => new MusicTimelineTrack(
                    Guid.NewGuid(),
                    label,
                    clips.Where(clip => clip.TrackObjectId == trackId).Select(clip =>
                    {
                        var source = mediaNames?.GetValueOrDefault(clip.MediaId);
                        return new MusicTimelineClip(
                            Guid.NewGuid(),
                            clip.MediaId,
                            source is null ? clip.MediaId.ToString() : Path.GetFileNameWithoutExtension(source),
                            null,
                            Math.Max(0, clip.TimelineStartMs) * timeRatio,
                            Math.Max(0, clip.BeginTrimMs) * timeRatio,
                            Math.Max(1, (clip.TimelineEndMs - Math.Max(0, clip.TimelineStartMs)) * timeRatio),
                            clip.SourceIdOffset,
                            FieldOffsets: clip.FieldOffsets,
                            PhysicalDurationMs: clip.SourceDurationMs,
                            PlaylistIndex: clip.PlaylistIndex,
                            HasFadeIn: clip.HasFadeIn,
                            HasFadeOut: clip.HasFadeOut,
                            RepeatsSource: clip.RepeatsSource,
                            FadeInMs: clip.FadeInMs,
                            FadeOutMs: clip.FadeOutMs);
                    }).ToArray(),
                    trackId,
                    segment.ObjectId,
                    segment.DurationMs * timeRatio));
        }).ToArray();
        var markers = segments.SelectMany(segment => segment.Markers.Select(marker => new
        {
            marker.Id,
            Name = MarkerName(marker.Id),
            Position = marker.PositionMs * timeRatio,
            marker.PositionOffset,
            SegmentObjectId = segment.ObjectId
        }))
            .GroupBy(marker => (marker.SegmentObjectId, marker.Id, RoundedPosition: Math.Round(marker.Position, 3)))
            .Select(group => new MusicTimelineMarker(
                group.Key.Id,
                group.Count() == 1 ? group.First().Name : $"{group.First().Name} x{group.Count()}",
                group.Average(marker => marker.Position),
                group.Key.SegmentObjectId,
                group.Select(marker => marker.PositionOffset).OfType<int>().Distinct().ToArray()))
            .OrderBy(marker => marker.PositionMs)
            .ToArray();
        var length = segments.Select(segment => segment.DurationMs * timeRatio).DefaultIfEmpty(1).Max();

        document.Reset(bpm, Math.Max(1, length), tracks, markers, snapEnabled: snapEnabled);

        return new MusicScopeTimelineImportResult(
            validation.ScopeObjectId,
            segments.Length,
            tracks.Length,
            tracks.Sum(track => track.Clips.Length),
            markers.Length,
            tracks.SelectMany(track => track.Clips).Select(clip => clip.MediaId).OfType<uint>().Distinct().Count());
    }

    private static string SegmentLabel(
        BnkTimelineSegment segment,
        IEnumerable<BnkTimelineClip> clips,
        IReadOnlyDictionary<uint, string>? mediaNames)
    {
        var names = clips.Where(clip => clip.SegmentObjectId == segment.ObjectId)
            .Select(clip => mediaNames?.GetValueOrDefault(clip.MediaId))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileNameWithoutExtension(name!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length switch
        {
            0 => segment.ObjectId.ToString(CultureInfo.InvariantCulture),
            1 => names[0],
            _ => $"{names[0]} (+{names.Length - 1})"
        };
    }

    private static string MarkerName(uint markerId) => markerId switch
    {
        EntryMarkerId => "Entry",
        ExitMarkerId => "Exit",
        _ => $"Cue {markerId}"
    };
}
