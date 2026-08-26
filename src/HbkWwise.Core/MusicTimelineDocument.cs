namespace HbkWwise.Core;

public sealed record MusicTimelineClip(
    Guid Id,
    uint? MediaId,
    string Name,
    string? SourcePath,
    double StartMs,
    double SourceOffsetMs,
    double DurationMs,
    int? SourceIdOffset = null,
    uint? ReplacementMediaId = null,
    BnkTimelineFieldOffsets? FieldOffsets = null,
    double? PhysicalDurationMs = null,
    int? PlaylistIndex = null,
    bool HasFadeIn = false,
    bool HasFadeOut = false,
    bool RepeatsSource = false,
    double FadeInMs = 0,
    double FadeOutMs = 0);

public sealed record MusicTimelineTrack(
    Guid Id,
    string Name,
    MusicTimelineClip[] Clips,
    uint? ObjectId = null,
    uint? SegmentObjectId = null,
    double? LengthMs = null,
    bool IsMuted = false,
    bool IsSolo = false,
    double Gain = 1);

public sealed record MusicTimelineMarker(
    uint Id,
    string Name,
    double PositionMs,
    uint? SegmentObjectId = null,
    int[]? PositionOffsets = null);

public enum MusicTimelineFadeKind
{
    FadeIn,
    FadeOut
}

public sealed record MusicTimelineFadeResult(
    MusicTimelineFadeKind Kind,
    double DurationMs,
    bool TrimmedClip);

public sealed class MusicTimelineDocument
{
    private readonly Stack<State> undo = new();
    private readonly Stack<State> redo = new();
    private Dictionary<uint, double> segmentBpms = [];
    private List<MusicTimelineTrack> tracks;

    public MusicTimelineDocument(
        double bpm = 120,
        int beatsPerBar = 4,
        int subdivisionsPerBeat = 1,
        bool snapEnabled = true,
        bool createDefaultTrack = true)
    {
        ValidateGrid(bpm, beatsPerBar, subdivisionsPerBeat);
        Bpm = bpm;
        BeatsPerBar = beatsPerBar;
        SubdivisionsPerBeat = subdivisionsPerBeat;
        SnapEnabled = snapEnabled;
        tracks = createDefaultTrack ? [new MusicTimelineTrack(Guid.NewGuid(), "Track 1", [])] : [];
        Markers = [];
    }

    public event Action? Changed;

    public double Bpm { get; private set; }

    public int BeatsPerBar { get; private set; }

    public int SubdivisionsPerBeat { get; private set; }

    public bool SnapEnabled { get; private set; }

    public double? TimelineLengthMs { get; private set; }

    public IReadOnlyList<MusicTimelineMarker> Markers { get; private set; }

    public IReadOnlyList<MusicTimelineTrack> Tracks => tracks;

    public IReadOnlyDictionary<uint, double> SegmentBpms => segmentBpms;

    public bool CanUndo => undo.Count > 0;

    public bool CanRedo => redo.Count > 0;

    public double BeatMilliseconds => 60_000 / Bpm;

    public double GridMilliseconds => BeatMilliseconds / SubdivisionsPerBeat;

    public double SegmentBpm(uint? segmentId) => segmentId is { } id && segmentBpms.TryGetValue(id, out var bpm)
        ? bpm
        : Bpm;

    public double BeatMillisecondsFor(uint? segmentId) => 60_000 / SegmentBpm(segmentId);

    public double GridMillisecondsFor(uint? segmentId) => BeatMillisecondsFor(segmentId) / SubdivisionsPerBeat;

    public double Snap(double timeMs, uint? segmentId = null) => SnapEnabled
        ? Math.Max(0, Math.Round(timeMs / GridMillisecondsFor(segmentId), MidpointRounding.AwayFromZero)
            * GridMillisecondsFor(segmentId))
        : Math.Max(0, timeMs);

    public double SnapNear(
        double timeMs,
        double toleranceMs,
        uint? segmentId = null,
        double? gridStepMs = null,
        bool includeGrid = true)
    {
        var value = Math.Max(0, timeMs);
        if (!SnapEnabled || toleranceMs <= 0)
        {
            return value;
        }

        if (gridStepMs is not null && (!double.IsFinite(gridStepMs.Value) || gridStepMs <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(gridStepMs));
        }

        var candidates = tracks.Where(track => track.SegmentObjectId == segmentId)
            .SelectMany(track => track.Clips)
            .SelectMany(clip => new[] { clip.StartMs, clip.StartMs + clip.DurationMs })
            .Concat(tracks.Where(track => track.SegmentObjectId == segmentId)
                .Select(track => track.LengthMs)
                .OfType<double>())
            .Concat(Markers.Where(marker => marker.SegmentObjectId == segmentId).Select(marker => marker.PositionMs))
            .Append(0)
            .Where(candidate => Math.Abs(candidate - value) <= toleranceMs)
            .ToList();
        if (includeGrid)
        {
            var step = gridStepMs ?? GridMillisecondsFor(segmentId);
            var grid = Math.Max(0, Math.Round(value / step, MidpointRounding.AwayFromZero) * step);
            if (Math.Abs(grid - value) <= toleranceMs)
            {
                candidates.Add(grid);
            }
        }

        return candidates.Count == 0
            ? value
            : candidates.MinBy(candidate => Math.Abs(candidate - value));
    }

    public void Reset(
        double bpm,
        double? timelineLengthMs,
        IReadOnlyCollection<MusicTimelineTrack> newTracks,
        IReadOnlyCollection<MusicTimelineMarker>? markers = null,
        int beatsPerBar = 4,
        int subdivisionsPerBeat = 1,
        bool snapEnabled = true,
        IReadOnlyDictionary<uint, double>? segmentTempos = null)
    {
        ValidateGrid(bpm, beatsPerBar, subdivisionsPerBeat);
        if (timelineLengthMs is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineLengthMs));
        }

        Bpm = bpm;
        BeatsPerBar = beatsPerBar;
        SubdivisionsPerBeat = subdivisionsPerBeat;
        SnapEnabled = snapEnabled;
        TimelineLengthMs = timelineLengthMs;
        Markers = markers?.OrderBy(marker => marker.PositionMs).ToArray() ?? [];
        tracks = [.. newTracks];
        segmentBpms = tracks.Select(track => track.SegmentObjectId)
            .OfType<uint>()
            .Distinct()
            .ToDictionary(id => id, _ => bpm);
        if (segmentTempos is not null)
        {
            foreach (var tempo in segmentTempos)
            {
                ValidateGrid(tempo.Value, beatsPerBar, subdivisionsPerBeat);
                if (segmentBpms.ContainsKey(tempo.Key))
                {
                    segmentBpms[tempo.Key] = tempo.Value;
                }
            }
        }

        undo.Clear();
        redo.Clear();

        Changed?.Invoke();
    }

    public Guid AddTrack(
        string? name = null,
        uint? objectId = null,
        uint? segmentObjectId = null,
        double? lengthMs = null)
    {
        var id = Guid.NewGuid();
        Change(() => tracks.Add(new MusicTimelineTrack(
            id,
            name ?? $"Track {tracks.Count + 1}",
            [],
            objectId,
            segmentObjectId,
            lengthMs)));
        return id;
    }

    public Guid InsertTrackAfter(
        Guid? precedingTrackId,
        string? name = null,
        uint? objectId = null,
        uint? segmentObjectId = null,
        double? lengthMs = null)
    {
        var index = precedingTrackId is { } trackId
            ? tracks.FindIndex(track => track.Id == trackId)
            : tracks.Count - 1;
        if (precedingTrackId is not null && index < 0)
        {
            throw new KeyNotFoundException($"Unknown track {precedingTrackId}.");
        }

        var id = Guid.NewGuid();
        Change(() => tracks.Insert(index + 1, new MusicTimelineTrack(
            id,
            name ?? $"Track {tracks.Count + 1}",
            [],
            objectId,
            segmentObjectId,
            lengthMs)));
        return id;
    }

    public void RemoveTrack(Guid trackId)
    {
        RequireTrack(trackId);
        Change(() => tracks.RemoveAll(track => track.Id == trackId));
    }

    public void SetTrackMuted(Guid trackId, bool muted)
    {
        var track = RequireTrack(trackId);
        if (track.IsMuted != muted || muted && track.IsSolo)
        {
            Change(() => ReplaceTrack(trackId, item => item with
            {
                IsMuted = muted,
                IsSolo = muted ? false : item.IsSolo
            }));
        }
    }

    public void SetTrackSolo(Guid trackId, bool solo)
    {
        var track = RequireTrack(trackId);
        if (track.IsSolo == solo && (!solo || tracks.Count(item => item.IsSolo) == 1))
        {
            return;
        }

        Change(() => tracks = tracks.Select(item => item with
        {
            IsSolo = item.Id == trackId && solo,
            IsMuted = item.Id == trackId && solo ? false : item.IsMuted
        }).ToList());
    }

    public void SetTrackGain(Guid trackId, double gain)
    {
        if (!double.IsFinite(gain) || gain is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(gain));
        }

        var track = RequireTrack(trackId);
        if (Math.Abs(track.Gain - gain) > 0.0001)
        {
            Change(() => ReplaceTrack(trackId, item => item with { Gain = gain }));
        }
    }

    public void Clear()
    {
        Bpm = 120;
        BeatsPerBar = 4;
        SubdivisionsPerBeat = 1;
        TimelineLengthMs = null;
        Markers = [];
        segmentBpms.Clear();
        tracks = [];
        undo.Clear();
        redo.Clear();

        Changed?.Invoke();
    }

    public Guid AddClip(
        Guid trackId,
        string name,
        double startMs,
        double durationMs,
        uint? mediaId = null,
        string? sourcePath = null,
        double? physicalDurationMs = null,
        int? sourceIdOffset = null,
        uint? replacementMediaId = null,
        BnkTimelineFieldOffsets? fieldOffsets = null,
        int? playlistIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (durationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        }

        var targetTrack = RequireTrack(trackId);
        var clip = new MusicTimelineClip(
            Guid.NewGuid(),
            mediaId,
            name,
            sourcePath,
            Snap(startMs, targetTrack.SegmentObjectId),
            0,
            durationMs,
            SourceIdOffset: sourceIdOffset,
            ReplacementMediaId: replacementMediaId,
            FieldOffsets: fieldOffsets,
            PhysicalDurationMs: physicalDurationMs,
            PlaylistIndex: playlistIndex);

        Change(() => ReplaceTrack(trackId, track => track with { Clips = [.. track.Clips, clip] }));

        return clip.Id;
    }

    public Guid DuplicateClip(Guid clipId)
    {
        var (track, clip) = FindClip(clipId);
        var duplicate = clip with
        {
            Id = Guid.NewGuid(),
            StartMs = clip.StartMs + clip.DurationMs,
            PlaylistIndex = null
        };
        Change(() => ReplaceTrack(track.Id, item => item with { Clips = [.. item.Clips, duplicate] }));

        return duplicate.Id;
    }

    public MusicTimelineClip ConstrainMove(
        Guid clipId,
        Guid targetTrackId,
        double startMs,
        double edgeToleranceMs = 0)
    {
        var (_, clip) = FindClip(clipId);
        var targetTrack = RequireTrack(targetTrackId);
        var snapped = SnapEndpoint(
            startMs,
            clip.DurationMs,
            clipId,
            edgeToleranceMs,
            targetTrack.SegmentObjectId);

        return clip with { StartMs = Math.Max(0, snapped) };
    }

    public void MoveClip(Guid clipId, Guid targetTrackId, double startMs, double edgeToleranceMs = 0)
    {
        var moved = ConstrainMove(clipId, targetTrackId, startMs, edgeToleranceMs);
        Change(() =>
        {
            RemoveClipCore(clipId);
            ReplaceTrack(targetTrackId, track => track with { Clips = [.. track.Clips, moved] });
        });
    }

    public void SetClipRenderedSource(
        Guid clipId,
        string sourcePath,
        uint replacementMediaId,
        double physicalDurationMs)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Rendered audio was not found.", source);
        }

        var (track, clip) = FindClip(clipId);
        if (clip.MediaId is null)
        {
            throw new InvalidOperationException("The clip has no Wwise media template.");
        }

        if (replacementMediaId == clip.MediaId || !double.IsFinite(physicalDurationMs) || physicalDurationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDurationMs));
        }

        Change(() => ReplaceTrack(track.Id, item => item with
        {
            Clips = item.Clips.Select(value => value.Id == clipId
                ? clip with
                {
                    SourcePath = source,
                    SourceOffsetMs = 0,
                    DurationMs = physicalDurationMs,
                    ReplacementMediaId = replacementMediaId,
                    PhysicalDurationMs = physicalDurationMs,
                    HasFadeIn = false,
                    HasFadeOut = false,
                    RepeatsSource = false,
                    FadeInMs = 0,
                    FadeOutMs = 0
                }
                : value).ToArray()
        }));
    }

    public MusicTimelineClip ConstrainResize(
        Guid clipId,
        double startMs,
        double sourceOffsetMs,
        double durationMs,
        double edgeToleranceMs = 0)
    {
        if (startMs < 0 || sourceOffsetMs < 0 || durationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        }

        var (track, clip) = FindClip(clipId);
        var oldEnd = clip.StartMs + clip.DurationMs;
        var trimmingStart = Math.Abs(oldEnd - (startMs + durationMs)) <= 0.01;

        const double minimumDuration = 1;
        if (trimmingStart)
        {
            var snappedStart = SnapBoundary(startMs, clipId, edgeToleranceMs, track.SegmentObjectId);
            var constrainedStart = Math.Clamp(snappedStart, 0, oldEnd - minimumDuration);

            return clip with
            {
                StartMs = constrainedStart,
                SourceOffsetMs = Math.Max(0, sourceOffsetMs + constrainedStart - startMs),
                DurationMs = oldEnd - constrainedStart,
                FadeInMs = Math.Min(clip.FadeInMs, oldEnd - constrainedStart),
                FadeOutMs = Math.Min(clip.FadeOutMs, oldEnd - constrainedStart)
            };
        }

        var desiredEnd = startMs + durationMs;
        var snappedEnd = SnapBoundary(desiredEnd, clipId, edgeToleranceMs, track.SegmentObjectId);
        var constrainedEnd = Math.Max(clip.StartMs + minimumDuration, snappedEnd);
        var constrainedDuration = constrainedEnd - clip.StartMs;

        return clip with
        {
            DurationMs = constrainedDuration,
            FadeInMs = Math.Min(clip.FadeInMs, constrainedDuration),
            FadeOutMs = Math.Min(clip.FadeOutMs, constrainedDuration)
        };
    }

    public void ResizeClip(
        Guid clipId,
        double startMs,
        double sourceOffsetMs,
        double durationMs,
        double edgeToleranceMs = 0)
    {
        var (track, clip) = FindClip(clipId);
        var resized = ConstrainResize(clipId, startMs, sourceOffsetMs, durationMs, edgeToleranceMs);
        Change(() => ReplaceTrack(track.Id, item => item with
        {
            Clips = item.Clips.Select(value => value.Id == clip.Id
                ? resized
                : value).ToArray()
        }));
    }

    public Guid SplitClip(Guid clipId, double positionMs)
    {
        var (track, clip) = FindClip(clipId);
        var split = Snap(positionMs, track.SegmentObjectId);
        var leftDuration = split - clip.StartMs;
        var rightDuration = clip.DurationMs - leftDuration;

        if (leftDuration <= 0 || rightDuration <= 0)
        {
            throw new InvalidOperationException("Split position must be inside the clip.");
        }

        var right = clip with
        {
            Id = Guid.NewGuid(),
            StartMs = split,
            SourceOffsetMs = clip.SourceOffsetMs + leftDuration,
            DurationMs = rightDuration,
            HasFadeIn = false,
            FadeInMs = 0,
            FadeOutMs = Math.Min(clip.FadeOutMs, rightDuration)
        };
        Change(() => ReplaceTrack(track.Id, item => item with
        {
            Clips = item.Clips.Select(value => value.Id == clip.Id
                    ? value with
                    {
                        DurationMs = leftDuration,
                        HasFadeOut = false,
                        FadeOutMs = 0,
                        FadeInMs = Math.Min(value.FadeInMs, leftDuration)
                    }
                    : value)
                .Append(right)
                .ToArray()
        }));

        return right.Id;
    }

    public void SetClipFades(Guid clipId, double fadeInMs, double fadeOutMs)
    {
        var (track, clip) = FindClip(clipId);
        if (!double.IsFinite(fadeInMs) || fadeInMs < 0 || fadeInMs > clip.DurationMs
            || !double.IsFinite(fadeOutMs) || fadeOutMs < 0 || fadeOutMs > clip.DurationMs)
        {
            throw new ArgumentOutOfRangeException(nameof(fadeInMs));
        }

        Change(() => ReplaceTrack(track.Id, item => item with
        {
            Clips = item.Clips.Select(value => value.Id == clipId
                ? value with
                {
                    HasFadeIn = fadeInMs > 0,
                    HasFadeOut = fadeOutMs > 0,
                    FadeInMs = fadeInMs,
                    FadeOutMs = fadeOutMs
                }
                : value).ToArray()
        }));
    }

    public void SetClipArrangement(
        Guid clipId,
        double startMs,
        double sourceOffsetMs,
        double durationMs,
        bool repeatsSource,
        double fadeInMs,
        double fadeOutMs)
    {
        var (track, clip) = FindClip(clipId);
        if (!double.IsFinite(startMs) || startMs < 0
            || !double.IsFinite(sourceOffsetMs) || sourceOffsetMs < 0
            || !double.IsFinite(durationMs) || durationMs <= 0
            || !double.IsFinite(fadeInMs) || fadeInMs < 0 || fadeInMs > durationMs
            || !double.IsFinite(fadeOutMs) || fadeOutMs < 0 || fadeOutMs > durationMs)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        }

        Change(() => ReplaceTrack(track.Id, item => item with
        {
            Clips = item.Clips.Select(value => value.Id == clipId
                ? clip with
                {
                    StartMs = Snap(startMs, track.SegmentObjectId),
                    SourceOffsetMs = sourceOffsetMs,
                    DurationMs = durationMs,
                    RepeatsSource = repeatsSource,
                    HasFadeIn = fadeInMs > 0,
                    HasFadeOut = fadeOutMs > 0,
                    FadeInMs = fadeInMs,
                    FadeOutMs = fadeOutMs
                }
                : value).ToArray()
        }));
    }

    public void RemoveClip(Guid clipId)
    {
        FindClip(clipId);
        Change(() => RemoveClipCore(clipId));
    }

    public void SetMarkerPosition(
        MusicTimelineMarker marker,
        double positionMs,
        double snapToleranceMs = 8,
        double? gridStepMs = null)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (!double.IsFinite(positionMs) || positionMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionMs));
        }

        var index = Markers.ToList().FindIndex(item => ReferenceEquals(item, marker)
            || item.Id == marker.Id
            && item.SegmentObjectId == marker.SegmentObjectId
            && Math.Abs(item.PositionMs - marker.PositionMs) <= 0.001);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Timeline marker {marker.Id} was not found.");
        }

        var segmentId = Markers[index].SegmentObjectId;
        var snapped = SnapNear(positionMs, snapToleranceMs, segmentId, gridStepMs);

        Change(() =>
        {
            var updated = Markers.ToArray();
            updated[index] = updated[index] with { PositionMs = snapped };
            Markers = updated.OrderBy(item => item.PositionMs).ToArray();
            if (marker.Name.StartsWith("Exit", StringComparison.OrdinalIgnoreCase))
            {
                tracks = tracks.Select(track => track.SegmentObjectId == segmentId
                    ? track with { LengthMs = Math.Max(1, snapped) }
                    : track).ToList();
                TimelineLengthMs = tracks.Select(track => track.LengthMs ?? 0)
                    .Concat(tracks.SelectMany(track => track.Clips)
                        .Select(clip => clip.StartMs + clip.DurationMs))
                    .DefaultIfEmpty(1)
                    .Max();
            }
        });
    }

    public MusicTimelineFadeResult MakeFadeFromSelection(
        Guid clipId,
        double selectionStartMs,
        double selectionEndMs,
        MusicTimelineFadeKind? requestedKind = null)
    {
        if (!double.IsFinite(selectionStartMs) || !double.IsFinite(selectionEndMs)
            || selectionEndMs - selectionStartMs <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(selectionEndMs));
        }

        var (track, clip) = FindClip(clipId);
        var clipStart = clip.StartMs;
        var clipEnd = clip.StartMs + clip.DurationMs;
        var start = Math.Clamp(selectionStartMs, clipStart, clipEnd);
        var end = Math.Clamp(selectionEndMs, clipStart, clipEnd);
        if (end - start <= 1)
        {
            throw new InvalidOperationException("The selection does not overlap the selected clip.");
        }

        var touchesStart = selectionStartMs <= clipStart + 1;
        var touchesEnd = selectionEndMs >= clipEnd - 1;
        if (requestedKind is not null
            && requestedKind is not MusicTimelineFadeKind.FadeIn and not MusicTimelineFadeKind.FadeOut)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedKind));
        }

        if (requestedKind is null && touchesStart && touchesEnd)
        {
            throw new InvalidOperationException("Select a shorter range at one end of the clip.");
        }

        var fadeIn = requestedKind == MusicTimelineFadeKind.FadeIn
            || requestedKind is null && (touchesStart || !touchesEnd && start - clipStart < clipEnd - end);
        var trimmed = fadeIn ? !touchesStart : !touchesEnd;
        var updated = fadeIn
            ? clip with
            {
                StartMs = trimmed ? start : clip.StartMs,
                SourceOffsetMs = trimmed ? clip.SourceOffsetMs + start - clipStart : clip.SourceOffsetMs,
                DurationMs = trimmed ? clipEnd - start : clip.DurationMs,
                HasFadeIn = true,
                FadeInMs = end - start,
                FadeOutMs = Math.Min(clip.FadeOutMs, trimmed ? clipEnd - start : clip.DurationMs)
            }
            : clip with
            {
                DurationMs = trimmed ? end - clipStart : clip.DurationMs,
                HasFadeOut = true,
                FadeOutMs = end - start,
                FadeInMs = Math.Min(clip.FadeInMs, trimmed ? end - clipStart : clip.DurationMs)
            };
        updated = updated with
        {
            HasFadeIn = updated.FadeInMs > 0,
            HasFadeOut = updated.FadeOutMs > 0
        };

        Change(() => ReplaceTrack(track.Id, item => item with
        {
            Clips = item.Clips.Select(value => value.Id == clipId ? updated : value).ToArray()
        }));

        return new MusicTimelineFadeResult(
            fadeIn ? MusicTimelineFadeKind.FadeIn : MusicTimelineFadeKind.FadeOut,
            end - start,
            trimmed);
    }

    public int ReplaceMediaReferences(
        uint mediaId,
        string sourcePath,
        uint? replacementMediaId = null,
        double? physicalDurationMs = null)
    {
        var replacement = Path.GetFullPath(sourcePath);
        if (!File.Exists(replacement))
        {
            throw new FileNotFoundException("Replacement audio was not found.", replacement);
        }

        if (replacementMediaId == mediaId)
        {
            throw new ArgumentException("A scope-local replacement must use a new media ID.", nameof(replacementMediaId));
        }

        if (physicalDurationMs is not null && (!double.IsFinite(physicalDurationMs.Value) || physicalDurationMs <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDurationMs));
        }

        var count = tracks.Sum(track => track.Clips.Count(clip => clip.MediaId == mediaId
            && (clip.ReplacementMediaId is null || clip.ReplacementMediaId == replacementMediaId)));
        if (count == 0)
        {
            throw new KeyNotFoundException($"Media {mediaId} is not used by this timeline.");
        }

        Change(() => tracks = tracks.Select(track => track with
        {
            Clips = track.Clips.Select(clip => clip.MediaId == mediaId
                    && (clip.ReplacementMediaId is null || clip.ReplacementMediaId == replacementMediaId)
                ? clip with
                {
                    Name = Path.GetFileNameWithoutExtension(replacement),
                    SourcePath = replacement,
                    ReplacementMediaId = replacementMediaId,
                    PhysicalDurationMs = physicalDurationMs ?? clip.PhysicalDurationMs
                }
                : clip).ToArray()
        }).ToList());

        return count;
    }

    public int ReplaceImportedMedia(uint replacementMediaId, string sourcePath, double physicalDurationMs)
    {
        var replacement = Path.GetFullPath(sourcePath);
        if (!File.Exists(replacement))
        {
            throw new FileNotFoundException("Imported audio was not found.", replacement);
        }

        if (replacementMediaId == 0 || !double.IsFinite(physicalDurationMs) || physicalDurationMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalDurationMs));
        }

        var count = tracks.Sum(track => track.Clips.Count(clip => clip.ReplacementMediaId == replacementMediaId));
        if (count == 0)
        {
            throw new KeyNotFoundException($"Imported media {replacementMediaId} is not used by this timeline.");
        }

        Change(() => tracks = tracks.Select(track => track with
        {
            Clips = track.Clips.Select(clip => clip.ReplacementMediaId == replacementMediaId
                ? clip with
                {
                    Name = Path.GetFileNameWithoutExtension(replacement),
                    SourcePath = replacement,
                    PhysicalDurationMs = physicalDurationMs
                }
                : clip).ToArray()
        }).ToList());

        return count;
    }

    public void SetGrid(double bpm, int beatsPerBar, int subdivisionsPerBeat)
    {
        ValidateGrid(bpm, beatsPerBar, subdivisionsPerBeat);
        if (Bpm == bpm && BeatsPerBar == beatsPerBar && SubdivisionsPerBeat == subdivisionsPerBeat)
        {
            return;
        }

        Change(() =>
        {
            Bpm = bpm;
            BeatsPerBar = beatsPerBar;
            SubdivisionsPerBeat = subdivisionsPerBeat;
        });
    }

    public void SetBpmAndScale(double bpm)
    {
        ValidateGrid(bpm, BeatsPerBar, SubdivisionsPerBeat);
        if (Bpm == bpm)
        {
            return;
        }

        var ratio = Bpm / bpm;
        Change(() =>
        {
            Bpm = bpm;
            segmentBpms = segmentBpms.Keys.ToDictionary(id => id, _ => bpm);
            TimelineLengthMs *= ratio;
            Markers = Markers.Select(marker => marker with { PositionMs = marker.PositionMs * ratio }).ToArray();
            tracks = tracks.Select(track => track with
            {
                LengthMs = track.LengthMs * ratio,
                Clips = track.Clips.Select(clip => clip with
                {
                    StartMs = clip.StartMs * ratio,
                    SourceOffsetMs = clip.SourceOffsetMs * ratio,
                    DurationMs = clip.DurationMs * ratio
                }).ToArray()
            }).ToList();
        });
    }

    public void SetSegmentBpmAndScale(uint segmentId, double bpm)
    {
        ValidateGrid(bpm, BeatsPerBar, SubdivisionsPerBeat);
        if (!tracks.Any(track => track.SegmentObjectId == segmentId))
        {
            throw new KeyNotFoundException($"Music segment {segmentId} is not present in this timeline.");
        }

        var oldBpm = SegmentBpm(segmentId);
        if (oldBpm == bpm)
        {
            return;
        }

        var ratio = oldBpm / bpm;
        Change(() =>
        {
            segmentBpms[segmentId] = bpm;
            Markers = Markers.Select(marker => marker.SegmentObjectId == segmentId
                ? marker with { PositionMs = marker.PositionMs * ratio }
                : marker).ToArray();
            tracks = tracks.Select(track => track.SegmentObjectId == segmentId
                ? track with
                {
                    LengthMs = track.LengthMs * ratio,
                    Clips = track.Clips.Select(clip => clip with
                    {
                        StartMs = clip.StartMs * ratio,
                        SourceOffsetMs = clip.SourceOffsetMs * ratio,
                        DurationMs = clip.DurationMs * ratio
                    }).ToArray()
                }
                : track).ToList();
            TimelineLengthMs = tracks.Select(track => track.LengthMs ?? 0)
                .Concat(tracks.SelectMany(track => track.Clips).Select(clip => clip.StartMs + clip.DurationMs))
                .Concat(Markers.Select(marker => marker.PositionMs))
                .DefaultIfEmpty(1)
                .Max();
        });
    }

    public void SetSnapEnabled(bool enabled)
    {
        if (SnapEnabled == enabled)
        {
            return;
        }

        Change(() => SnapEnabled = enabled);
    }

    public (MusicTimelineTrack Track, MusicTimelineClip Clip) FindClip(Guid clipId)
    {
        foreach (var track in tracks)
        {
            var clip = track.Clips.FirstOrDefault(item => item.Id == clipId);
            if (clip is not null)
            {
                return (track, clip);
            }
        }

        throw new KeyNotFoundException($"Timeline clip {clipId} was not found.");
    }

    public void Undo()
    {
        if (undo.TryPop(out var state))
        {
            redo.Push(Capture());
            Restore(state);
        }
    }

    public void Redo()
    {
        if (redo.TryPop(out var state))
        {
            undo.Push(Capture());
            Restore(state);
        }
    }

    private void Change(Action action)
    {
        var state = Capture();
        action();
        undo.Push(state);
        redo.Clear();

        Changed?.Invoke();
    }

    private State Capture() => new(
        Bpm,
        BeatsPerBar,
        SubdivisionsPerBeat,
        SnapEnabled,
        TimelineLengthMs,
        [.. Markers],
        [.. tracks],
        new Dictionary<uint, double>(segmentBpms));

    private void Restore(State state)
    {
        Bpm = state.Bpm;
        BeatsPerBar = state.BeatsPerBar;
        SubdivisionsPerBeat = state.SubdivisionsPerBeat;
        SnapEnabled = state.SnapEnabled;
        TimelineLengthMs = state.TimelineLengthMs;
        Markers = state.Markers;
        tracks = [.. state.Tracks];
        segmentBpms = new Dictionary<uint, double>(state.SegmentBpms);

        Changed?.Invoke();
    }

    private void RemoveClipCore(Guid clipId)
    {
        tracks = tracks.Select(track => track with
        {
            Clips = track.Clips.Where(clip => clip.Id != clipId).ToArray()
        }).ToList();
    }

    private void ReplaceTrack(Guid trackId, Func<MusicTimelineTrack, MusicTimelineTrack> replacement)
    {
        var index = tracks.FindIndex(track => track.Id == trackId);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Timeline track {trackId} was not found.");
        }

        tracks[index] = replacement(tracks[index]);
    }

    private MusicTimelineTrack RequireTrack(Guid trackId) => tracks.FirstOrDefault(track => track.Id == trackId)
        ?? throw new KeyNotFoundException($"Timeline track {trackId} was not found.");

    private double SnapEndpoint(
        double startMs,
        double durationMs,
        Guid clipId,
        double toleranceMs,
        uint? segmentId)
    {
        if (!SnapEnabled || toleranceMs <= 0)
        {
            return toleranceMs <= 0 ? Snap(startMs, segmentId) : Math.Max(0, startMs);
        }

        var boundaries = ClipBoundaries(clipId, segmentId);
        var candidates = boundaries.SelectMany(boundary => new[] { boundary, boundary - durationMs })
            .Append(Snap(startMs, segmentId))
            .Where(candidate => candidate >= 0)
            .Where(candidate => Math.Abs(candidate - startMs) <= toleranceMs)
            .ToArray();

        return candidates.Length == 0
            ? Math.Max(0, startMs)
            : candidates.MinBy(candidate => Math.Abs(candidate - startMs));
    }

    private double SnapBoundary(double value, Guid clipId, double toleranceMs, uint? segmentId)
    {
        if (!SnapEnabled || toleranceMs <= 0)
        {
            return toleranceMs <= 0 ? Snap(value, segmentId) : Math.Max(0, value);
        }

        var candidates = ClipBoundaries(clipId, segmentId).Append(Snap(value, segmentId))
            .Where(candidate => Math.Abs(candidate - value) <= toleranceMs)
            .ToArray();
        return candidates.Length == 0
            ? Math.Max(0, value)
            : candidates.MinBy(candidate => Math.Abs(candidate - value));
    }

    private IEnumerable<double> ClipBoundaries(Guid excludedClipId, uint? segmentId) => tracks
        .Where(track => track.SegmentObjectId == segmentId)
        .SelectMany(track => track.Clips)
        .Where(clip => clip.Id != excludedClipId)
        .SelectMany(clip => new[] { clip.StartMs, clip.StartMs + clip.DurationMs });

    private static void ValidateGrid(double bpm, int beatsPerBar, int subdivisionsPerBeat)
    {
        if (bpm is < 20 or > 400)
        {
            throw new ArgumentOutOfRangeException(nameof(bpm));
        }

        if (beatsPerBar is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerBar));
        }

        if (subdivisionsPerBeat is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(subdivisionsPerBeat));
        }
    }

    private sealed record State(
        double Bpm,
        int BeatsPerBar,
        int SubdivisionsPerBeat,
        bool SnapEnabled,
        double? TimelineLengthMs,
        MusicTimelineMarker[] Markers,
        MusicTimelineTrack[] Tracks,
        Dictionary<uint, double> SegmentBpms);
}
