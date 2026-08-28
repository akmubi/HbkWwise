namespace HbkWwise.Core;

public sealed record BnkTimelineRebasedEdits(
    BnkTimelineClipEdit[]? TimelineEdits,
    BnkTrackPlaylistEdit[]? PlaylistEdits,
    BnkTimelineMarkerEdit[]? MarkerEdits,
    BnkTimelineSegmentDurationEdit[]? SegmentDurationEdits);

public static class BnkTimelineEditRebaser
{
    public static BnkTimelineRebasedEdits Rebase(
        BnkTimelineValidation authored,
        IReadOnlyCollection<BnkTimelineClipEdit>? timelineEdits,
        IReadOnlyCollection<BnkTrackPlaylistEdit>? playlistEdits,
        IReadOnlyCollection<BnkTimelineMarkerEdit>? markerEdits,
        IReadOnlyCollection<BnkTimelineSegmentDurationEdit>? segmentDurationEdits)
    {
        ArgumentNullException.ThrowIfNull(authored);

        return new BnkTimelineRebasedEdits(
            timelineEdits?.Select(edit => Rebase(authored, edit)).ToArray(),
            playlistEdits?.Select(edit => new BnkTrackPlaylistEdit(
                edit.TrackObjectId,
                edit.Items.Select(item => Rebase(authored, item)).ToArray())).ToArray(),
            markerEdits?.Select(edit => Rebase(authored, edit)).ToArray(),
            segmentDurationEdits?.Select(edit => Rebase(authored, edit)).ToArray());
    }

    private static BnkTimelineClipEdit Rebase(BnkTimelineValidation authored, BnkTimelineClipEdit edit)
    {
        if (edit.Anchor is null)
        {
            return edit;
        }

        var clip = FindClip(authored, edit.Anchor);
        return edit with { SourceIdOffset = RequiredSourceOffset(clip, edit.Anchor) };
    }

    private static BnkTrackPlaylistItemEdit Rebase(
        BnkTimelineValidation authored,
        BnkTrackPlaylistItemEdit edit)
    {
        if (edit.OriginalAnchor is null)
        {
            return edit;
        }

        var clip = FindClip(authored, edit.OriginalAnchor);
        return edit with { OriginalSourceIdOffset = RequiredSourceOffset(clip, edit.OriginalAnchor) };
    }

    private static BnkTimelineMarkerEdit Rebase(
        BnkTimelineValidation authored,
        BnkTimelineMarkerEdit edit)
    {
        if (edit.Anchor is null)
        {
            return edit;
        }

        var marker = authored.Segments
            .Where(segment => segment.ObjectId == edit.Anchor.SegmentObjectId)
            .SelectMany(segment => segment.Markers)
            .SingleOrDefault(item => item.Id == edit.Anchor.MarkerId)
            ?? throw new InvalidDataException(
                $"Music Segment {edit.Anchor.SegmentObjectId} has no authored cue {edit.Anchor.MarkerId}.");

        return marker.PositionOffset is { } offset
            ? edit with { PositionOffset = offset }
            : throw new InvalidDataException(
                $"Cue {edit.Anchor.MarkerId} in Music Segment {edit.Anchor.SegmentObjectId} has no bank offset.");
    }

    private static BnkTimelineSegmentDurationEdit Rebase(
        BnkTimelineValidation authored,
        BnkTimelineSegmentDurationEdit edit)
    {
        if (edit.SegmentObjectId is not { } segmentId)
        {
            return edit;
        }

        var segment = authored.Segments.SingleOrDefault(item => item.ObjectId == segmentId)
            ?? throw new InvalidDataException($"Music Segment {segmentId} is no longer in the timing scope.");

        return segment.DurationOffset is { } offset
            ? edit with { DurationOffset = offset }
            : throw new InvalidDataException($"Music Segment {segmentId} has no bank duration offset.");
    }

    private static BnkTimelineClip FindClip(
        BnkTimelineValidation authored,
        BnkTimelineClipAnchor anchor)
    {
        var trackClips = authored.Clips.Where(clip =>
                clip.TrackObjectId == anchor.TrackObjectId
                && (anchor.SegmentObjectId is null || clip.SegmentObjectId == anchor.SegmentObjectId))
            .DistinctBy(clip => clip.SourceIdOffset)
            .ToArray();

        var candidates = trackClips.Where(clip => clip.PlaylistIndex == anchor.PlaylistIndex).ToArray();
        var exact = candidates.Where(clip => clip.MediaId == anchor.MediaId).ToArray();
        if (exact.Length == 1)
        {
            return exact[0];
        }

        var moved = trackClips.Where(clip => clip.MediaId == anchor.MediaId).ToArray();
        if (moved.Length == 1)
        {
            return moved[0];
        }

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        throw new InvalidDataException(
            $"Music Track {anchor.TrackObjectId} playlist item {anchor.PlaylistIndex + 1} "
            + $"in Music Segment {anchor.SegmentObjectId?.ToString() ?? "unknown"} no longer has one authored clip.");
    }

    private static int RequiredSourceOffset(BnkTimelineClip clip, BnkTimelineClipAnchor anchor) =>
        clip.SourceIdOffset
        ?? throw new InvalidDataException(
            $"Music Track {anchor.TrackObjectId} playlist item {anchor.PlaylistIndex + 1} has no bank source offset.");
}
