using System.Globalization;
using System.Xml.Linq;

namespace HbkWwise.Core;

public enum BnkTimelineSeverity
{
    Warning,
    Error
}

public sealed record BnkTimelineMarker(uint Id, double PositionMs, int? PositionOffset = null);

public sealed record BnkTimelineSegment(
    uint ObjectId,
    double DurationMs,
    uint[] TrackObjectIds,
    BnkTimelineMarker[] Markers,
    int? DurationOffset = null);

public sealed record BnkTimelineFieldOffsets(
    int? PlayAt,
    int? BeginTrim,
    int? EndTrim,
    int? SourceDuration);

public sealed record BnkTimelineClip(
    uint TrackObjectId,
    uint? SegmentObjectId,
    uint MediaId,
    int? SourceIdOffset,
    double PlayAtMs,
    double BeginTrimMs,
    double EndTrimMs,
    double SourceDurationMs,
    double TimelineStartMs,
    double TimelineEndMs,
    bool RepeatsSource,
    BnkTimelineFieldOffsets? FieldOffsets = null,
    int PlaylistIndex = 0,
    bool HasFadeIn = false,
    bool HasFadeOut = false,
    uint SubTrackId = 0,
    uint EventId = 0,
    double FadeInMs = 0,
    double FadeOutMs = 0);

public sealed record BnkTimelineTransition(
    uint ObjectId,
    int SyncType,
    uint SourceCueId,
    uint DestinationCueId);

public sealed record BnkTimelineLoop(
    uint ObjectId,
    uint SegmentId,
    int Count,
    int Minimum,
    int Maximum);

public sealed record BnkTimelineIssue(
    BnkTimelineSeverity Severity,
    string Code,
    uint ObjectId,
    uint? MediaId,
    string Message);

public sealed record BnkTimelineValidation(
    uint ScopeObjectId,
    double Ratio,
    BnkTimelineSegment[] Segments,
    BnkTimelineClip[] Clips,
    BnkTimelineTransition[] Transitions,
    BnkTimelineLoop[] Loops,
    BnkDurationValidation DurationValidation,
    BnkTimelineIssue[] Issues)
{
    public bool HasErrors => Issues.Any(item => item.Severity == BnkTimelineSeverity.Error);
}

public static class BnkTimelineValidator
{
    private const uint EntryMarkerId = 43573010;
    private const uint ExitMarkerId = 1539036744;

    public static BnkTimelineValidation Validate(
        string wwiserXmlPath,
        uint scopeObjectId,
        IReadOnlyDictionary<uint, double> replacementDurationsMs,
        double fromBpm,
        double newBpm,
        double toleranceMs = 1,
        string? eventNameOrId = null)
    {
        if (fromBpm <= 0 || newBpm <= 0 || toleranceMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromBpm));
        }

        var ratio = fromBpm / newBpm;
        var scope = BnkRetimer.FindTimingScopes(wwiserXmlPath, eventNameOrId)
            .SingleOrDefault(item => item.ObjectId == scopeObjectId)
            ?? throw new InvalidDataException($"Active timing scope {scopeObjectId} was not found.");
        var graph = WwiserHircGraph.Load(wwiserXmlPath);
        var objects = ReadObjects(XDocument.Load(wwiserXmlPath));
        var allowed = scope.ObjectIds.ToHashSet();
        var segments = allowed
            .Where(id => graph.Objects.TryGetValue(id, out var item) && item.Type == 10 && objects.ContainsKey(id))
            .Select(id => ReadSegment(id, graph.Objects[id], objects[id], ratio))
            .OrderBy(item => item.ObjectId)
            .ToArray();
        var trackSegments = segments
            .SelectMany(segment => segment.TrackObjectIds.Select(trackId => (trackId, segment.ObjectId)))
            .GroupBy(item => item.trackId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ObjectId).Distinct().ToArray());
        var clips = allowed
            .Where(id => graph.Objects.TryGetValue(id, out var item) && item.Type == 11 && objects.ContainsKey(id))
            .SelectMany(id => ReadClips(id, objects[id], trackSegments.GetValueOrDefault(id), ratio))
            .OrderBy(item => item.SegmentObjectId)
            .ThenBy(item => item.TrackObjectId)
            .ThenBy(item => item.MediaId)
            .ToArray();
        var transitions = allowed.Where(objects.ContainsKey)
            .SelectMany(id => ReadTransitions(id, objects[id]))
            .ToArray();
        var loops = allowed.Where(objects.ContainsKey)
            .SelectMany(id => ReadLoops(id, objects[id]))
            .ToArray();
        var duration = BnkDurationValidator.Validate(
            wwiserXmlPath,
            scopeObjectId,
            replacementDurationsMs,
            toleranceMs,
            eventNameOrId);
        var issues = FindIssues(segments, clips, transitions, duration, toleranceMs);

        return new BnkTimelineValidation(
            scopeObjectId,
            ratio,
            segments,
            clips,
            transitions,
            loops,
            duration,
            issues);
    }

    private static BnkTimelineSegment ReadSegment(
        uint id,
        WwiserHircObject graphObject,
        XElement node,
        double ratio)
    {
        var initial = NamedObjects(node, "MusicSegmentInitialValues").FirstOrDefault()
            ?? throw new InvalidDataException($"Music segment {id} has no initial values.");
        var durationField = DirectField(initial, "fDuration");
        var duration = DoubleValue(durationField) * ratio;
        var markers = NamedObjects(initial, "AkMusicMarkerWwise")
            .Select(marker =>
            {
                var position = DirectField(marker, "fPosition");
                return new BnkTimelineMarker(
                    UIntValue(marker, "id"),
                    DoubleValue(position) * ratio,
                    FieldOffset(position));
            })
            .OrderBy(marker => marker.PositionMs)
            .ToArray();

        return new BnkTimelineSegment(id, duration, graphObject.ChildIds, markers, FieldOffset(durationField));
    }

    private static IEnumerable<BnkTimelineClip> ReadClips(
        uint trackId,
        XElement node,
        uint[]? segmentIds,
        double ratio)
    {
        var parents = segmentIds is { Length: > 0 } ? segmentIds.Cast<uint?>().ToArray() : [null];
        var automation = NamedObjects(node, "AkClipAutomation")
            .Select(item => (
                ClipIndex: IntValue(item, "uClipIndex"),
                Type: IntValue(item, "eAutoType"),
                Times: NamedObjects(item, "AkRTPCGraphPoint")
                    .Select(point => DoubleValue(point, "From")).Order().ToArray()))
            .GroupBy(item => item.ClipIndex)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var fadeIn = group.Where(item => item.Type == 3).Select(item => item.Times).FirstOrDefault();
                    var fadeOut = group.Where(item => item.Type == 4).Select(item => item.Times).FirstOrDefault();

                    return (
                        FadeIn: fadeIn is not null,
                        FadeOut: fadeOut is not null,
                        FadeInMs: Range(fadeIn) * 1000,
                        FadeOutMs: LastRange(fadeOut) * 1000);
                });
        var playlist = NamedObjects(node, "AkTrackSrcInfo").ToArray();

        for (var playlistIndex = 0; playlistIndex < playlist.Length; playlistIndex++)
        {
            var track = playlist[playlistIndex];
            var sourceId = DirectField(track, "sourceID");
            var mediaId = sourceId is null ? 0 : UIntValue(track, "sourceID");

            if (mediaId == 0)
            {
                continue;
            }

            var playField = DirectField(track, "fPlayAt");
            var beginField = DirectField(track, "fBeginTrimOffset");
            var endField = DirectField(track, "fEndTrimOffset");
            var durationField = DirectField(track, "fSrcDuration");
            var subTrackId = UIntValue(track, "trackID");
            var eventId = UIntValue(track, "eventID");
            var playAt = DoubleValue(playField) * ratio;
            var beginTrim = DoubleValue(beginField) * ratio;
            var endTrim = DoubleValue(endField) * ratio;
            var sourceDuration = DoubleValue(durationField);

            var (start, end, repeats) = ClipBounds(playAt, beginTrim, endTrim, sourceDuration);
            var fades = automation.GetValueOrDefault(playlistIndex);
            foreach (var segmentId in parents)
            {
                yield return new BnkTimelineClip(
                    trackId,
                    segmentId,
                    mediaId,
                    Offset(sourceId!),
                    playAt,
                    beginTrim,
                    endTrim,
                    sourceDuration,
                    start,
                    end,
                    repeats,
                    new BnkTimelineFieldOffsets(
                        FieldOffset(playField),
                        FieldOffset(beginField),
                        FieldOffset(endField),
                        FieldOffset(durationField)),
                    playlistIndex,
                    fades.FadeIn,
                    fades.FadeOut,
                    subTrackId,
                    eventId,
                    fades.FadeInMs,
                    fades.FadeOutMs);
            }
        }
    }

    private static double Range(double[]? values) => values is { Length: > 1 } ? values[^1] - values[0] : 0;

    private static double LastRange(double[]? values) => values is { Length: > 1 } ? values[^1] - values[^2] : 0;

    private static (double Start, double End, bool Repeats) ClipBounds(
        double playAt,
        double beginTrim,
        double endTrim,
        double sourceDuration)
    {
        var body = sourceDuration;
        var start = playAt;
        var trimBegin = 0d;
        var trimEnd = 0d;
        var repeats = false;

        if (beginTrim >= 0)
        {
            trimBegin = beginTrim;
            start += beginTrim;
        }
        else if (sourceDuration > 0)
        {
            var repeat = Math.Abs(beginTrim);
            var trim = Math.Abs(beginTrim % sourceDuration);

            body += repeat + trim;
            trimBegin = trim;
            start -= repeat;
            repeats = true;
        }

        if (endTrim <= 0)
        {
            trimEnd = Math.Abs(endTrim);
        }
        else
        {
            body += endTrim;
            repeats = true;
        }

        return (start, start + body - trimBegin - trimEnd, repeats);
    }

    private static BnkTimelineTransition[] ReadTransitions(uint objectId, XElement node) =>
        NamedObjects(node, "AkMusicTransitionRule")
            .Select(rule =>
            {
                var source = NamedObjects(rule, "AkMusicTransSrcRule").FirstOrDefault();
                var destination = NamedObjects(rule, "AkMusicTransDstRule").FirstOrDefault();

                return new BnkTimelineTransition(
                    objectId,
                    source is null ? 0 : IntValue(source, "eSyncType"),
                    source is null ? 0 : UIntValue(source, "uCueFilterHash"),
                    destination is null ? 0 : UIntValue(destination, "uJumpToID"));
            })
            .ToArray();

    private static BnkTimelineLoop[] ReadLoops(uint objectId, XElement node) =>
        NamedObjects(node, "AkMusicRanSeqPlaylistItem")
            .Select(item => new BnkTimelineLoop(
                objectId,
                UIntValue(item, "SegmentID"),
                IntValue(item, "Loop"),
                IntValue(item, "LoopMin"),
                IntValue(item, "LoopMax")))
            .ToArray();

    private static BnkTimelineIssue[] FindIssues(
        IReadOnlyCollection<BnkTimelineSegment> segments,
        IReadOnlyCollection<BnkTimelineClip> clips,
        IReadOnlyCollection<BnkTimelineTransition> transitions,
        BnkDurationValidation duration,
        double toleranceMs)
    {
        var issues = new List<BnkTimelineIssue>();
        var segmentMap = segments.ToDictionary(item => item.ObjectId);

        foreach (var segment in segments)
        {
            var entry = segment.Markers.FirstOrDefault(item => item.Id == EntryMarkerId);
            var exit = segment.Markers.FirstOrDefault(item => item.Id == ExitMarkerId);

            if (entry is null || exit is null)
            {
                issues.Add(Issue(BnkTimelineSeverity.Error, "MISSING_CUE", segment.ObjectId, null,
                    "Music segment is missing its entry or exit marker."));
            }
            else if (entry.PositionMs > exit.PositionMs + toleranceMs)
            {
                issues.Add(Issue(BnkTimelineSeverity.Error, "CUE_ORDER", segment.ObjectId, null,
                    "Entry marker is after the exit marker."));
            }

            foreach (var marker in segment.Markers.Where(item =>
                         item.PositionMs < -toleranceMs || item.PositionMs > segment.DurationMs + toleranceMs))
            {
                issues.Add(Issue(BnkTimelineSeverity.Error, "MARKER_BOUNDS", segment.ObjectId, null,
                    $"Marker {marker.Id} at {marker.PositionMs:0.###} ms is outside segment duration {segment.DurationMs:0.###} ms."));
            }
        }

        foreach (var clip in clips)
        {
            if (clip.TimelineEndMs < clip.TimelineStartMs - toleranceMs)
            {
                issues.Add(Issue(BnkTimelineSeverity.Error, "CLIP_LENGTH", clip.TrackObjectId, clip.MediaId,
                    "Clip trims produce a negative timeline length."));
            }

            if (clip.SegmentObjectId is null || !segmentMap.TryGetValue(clip.SegmentObjectId.Value, out var segment))
            {
                issues.Add(Issue(BnkTimelineSeverity.Warning, "UNBOUND_TRACK", clip.TrackObjectId, clip.MediaId,
                    "Track is not directly bound to a segment inside this timing scope."));
                continue;
            }

            if (clip.TimelineStartMs < -toleranceMs || clip.TimelineEndMs > segment.DurationMs + toleranceMs)
            {
                issues.Add(Issue(BnkTimelineSeverity.Error, "CLIP_BOUNDS", clip.TrackObjectId, clip.MediaId,
                    $"Clip interval {clip.TimelineStartMs:0.###}..{clip.TimelineEndMs:0.###} ms is outside segment "
                    + $"{segment.ObjectId} duration {segment.DurationMs:0.###} ms."));
            }
        }

        var markerIds = segments.SelectMany(item => item.Markers).Select(item => item.Id).ToHashSet();
        foreach (var transition in transitions)
        {
            if (transition.SourceCueId != 0 && !markerIds.Contains(transition.SourceCueId))
            {
                issues.Add(Issue(BnkTimelineSeverity.Warning, "TRANSITION_SOURCE_CUE", transition.ObjectId, null,
                    $"Transition source cue {transition.SourceCueId} is absent from this timing scope."));
            }

            if (transition.DestinationCueId != 0 && !markerIds.Contains(transition.DestinationCueId))
            {
                issues.Add(Issue(BnkTimelineSeverity.Warning, "TRANSITION_DESTINATION_CUE", transition.ObjectId, null,
                    $"Transition destination cue {transition.DestinationCueId} is absent from this timing scope."));
            }
        }

        foreach (var check in duration.Checks)
        {
            var severity = check.Fit == BnkDurationFit.TooShort ? BnkTimelineSeverity.Error : BnkTimelineSeverity.Warning;
            if (check.Fit != BnkDurationFit.Match)
            {
                issues.Add(Issue(severity, $"MEDIA_{check.Fit.ToString().ToUpperInvariant()}", 0, check.MediaId,
                    $"Replacement duration is {check.Fit}."));
            }
        }

        return issues.OrderByDescending(item => item.Severity).ThenBy(item => item.Code).ThenBy(item => item.ObjectId).ToArray();
    }

    private static BnkTimelineIssue Issue(
        BnkTimelineSeverity severity,
        string code,
        uint objectId,
        uint? mediaId,
        string message) => new(severity, code, objectId, mediaId, message);

    private static Dictionary<uint, XElement> ReadObjects(XDocument document)
    {
        var hirc = document.Descendants().FirstOrDefault(node => Is(node, "object", "obj") && Name(node) == "HircChunk")
            ?? throw new InvalidDataException("HircChunk was not found in the wwiser XML.");
        var loaded = hirc.Elements().FirstOrDefault(node => Is(node, "list", "lst") && Name(node) == "listLoadedItem")
            ?? throw new InvalidDataException("HIRC listLoadedItem was not found in the wwiser XML.");

        return loaded.Elements()
            .Where(node => Is(node, "object", "obj"))
            .Select(node => (Node: node, Id: UIntValue(node, "ulID")))
            .Where(item => item.Id != 0)
            .ToDictionary(item => item.Id, item => item.Node);
    }

    private static IEnumerable<XElement> NamedObjects(XElement node, string name) =>
        node.Descendants().Where(item => Is(item, "object", "obj") && Name(item) == name);

    private static XElement? DirectField(XElement node, string name) => node.Elements()
        .FirstOrDefault(item => Is(item, "field", "fld") && Name(item) == name);

    private static uint UIntValue(XElement node, string name) => DirectField(node, name) is { } field
        ? uint.Parse(Value(field)!, NumberStyles.Integer, CultureInfo.InvariantCulture)
        : 0;

    private static int IntValue(XElement node, string name) => DirectField(node, name) is { } field
        ? int.Parse(Value(field)!, NumberStyles.Integer, CultureInfo.InvariantCulture)
        : 0;

    private static double DoubleValue(XElement node, string name) => DirectField(node, name) is { } field
        ? double.Parse(Value(field)!, NumberStyles.Float, CultureInfo.InvariantCulture)
        : 0;

    private static double DoubleValue(XElement? field) => field is null
        ? 0
        : double.Parse(Value(field)!, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static int? FieldOffset(XElement? field) => field is null ? null : Offset(field);

    private static string? Name(XElement node) => node.Attribute("name")?.Value ?? node.Attribute("na")?.Value;

    private static string? Value(XElement node) => node.Attribute("value")?.Value ?? node.Attribute("va")?.Value;

    private static int? Offset(XElement node) => (node.Attribute("offset")?.Value ?? node.Attribute("of")?.Value) is { } value
        ? value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
        : null;

    private static bool Is(XElement node, string longName, string shortName) =>
        node.Name.LocalName is var name && (name == longName || name == shortName);
}
