using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using HbkWwise.Core;

namespace HbkWwise.Gui;

public sealed record TimelineViewState(
    double PixelsPerSecond,
    double HorizontalOffsetMs,
    double VerticalOffset,
    double HeaderWidth,
    double PlayheadMs,
    double? SelectionStartMs,
    double? SelectionEndMs,
    uint? FocusedSegmentId,
    uint? AuditionSegmentId,
    Guid? SelectedTrackId,
    Guid? SelectedClipId,
    uint? SelectedMediaId);

public sealed class TimelineControl : Panel
{
    private const double MinimumHeaderWidth = 300;
    private const double RulerHeight = 50;
    private const double SegmentHeaderHeight = 46;
    private const double TrackHeight = 102;
    private const double HandleWidth = 7;
    private const double SeparatorWidth = 20;
    private readonly MusicTimelineDocument document;
    private readonly TimelineRenderer renderer;
    private readonly Dictionary<string, WaveformEnvelope> waveforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, SelectableTextBlock> trackNameLabels = [];
    private readonly List<MarkerLabelHit> markerLabelHits = [];
    private HashSet<uint> nonAudioMediaIds = [];
    private HashSet<Guid> dirtyTrackIds = [];
    private HashSet<uint>? visibleSegmentIds;
    private DragState? drag;
    private MarkerDragState? markerDrag;
    private double? previewMarkerPositionMs;
    private SelectionDragState? selectionDrag;
    private MusicTimelineClip? previewClip;
    private Guid? previewTrackId;
    private Guid? gainDragTrackId;
    private double headerWidth = 360;
    private double horizontalOffsetMs;
    private double verticalOffset;
    private double pixelsPerSecond = 100;
    private bool resizingHeader;
    private bool userSizedHeader;
    private bool showMarkers;
    private bool standaloneAudioMode;
    private string? audioColorScope;
    private string? sourceEventName;
    private HashSet<uint> metronomeSegments = [];
    private uint? focusedSegmentId;
    private uint? auditionSegmentId;

    public TimelineControl(MusicTimelineDocument document)
    {
        this.document = document;
        renderer = new TimelineRenderer(this)
        {
            IsHitTestVisible = false
        };
        Children.Add(renderer);
        SelectedTrackId = document.Tracks.FirstOrDefault()?.Id;
        Focusable = true;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        document.Changed += DocumentChanged;
        SizeChanged += (_, _) =>
        {
            InvalidateArrange();
            NotifyBpmEditorPlacement();
        };
        SyncTrackNameLabels();
    }

    public event Action? SelectionChanged;
    public event Action<string>? StatusChanged;
    public event Action<double>? SeekRequested;
    public event Action<double>? ZoomChanged;
    public event Action? PlayPauseRequested;
    public event Action? TrackMixChanged;
    public event Action? AuditionSegmentChanged;
    public event Action<uint>? SegmentBpmEditRequested;
    public event Action<uint?, Rect?>? SegmentBpmEditorPlacementChanged;
    public Guid? SelectedClipId { get; private set; }
    public uint? SelectedMediaId { get; private set; }
    public Guid? SelectedTrackId { get; private set; }
    public double PlayheadMs { get; private set; }
    public double? SelectionStartMs { get; private set; }
    public double? SelectionEndMs { get; private set; }
    public uint? FocusedSegmentId => focusedSegmentId;
    public uint? AuditionSegmentId => auditionSegmentId ?? focusedSegmentId;
    public int VisibleSegmentCount => SegmentGroups().Count;
    public IReadOnlyList<MusicTimelineTrack> VisibleTracks => visibleSegmentIds is null
        ? document.Tracks
        : document.Tracks.Where(track => track.SegmentObjectId is { } id && visibleSegmentIds.Contains(id)).ToArray();
    public IReadOnlySet<uint>? VisibleSegmentIds => visibleSegmentIds;
    public IReadOnlySet<uint> MetronomeSegments => metronomeSegments;
    public bool StandaloneAudioMode => standaloneAudioMode;

    public bool ShowMarkers
    {
        get => showMarkers;
        set
        {
            showMarkers = value;
            InvalidateVisual();
        }
    }

    public void SetStandaloneAudioMode(bool enabled)
    {
        standaloneAudioMode = enabled;
        if (enabled)
        {
            focusedSegmentId = null;
            auditionSegmentId = null;
        }

        verticalOffset = 0;
        InvalidateArrange();
        InvalidateVisual();
        NotifyBpmEditorPlacement();
    }

    public double PixelsPerSecond
    {
        get => pixelsPerSecond;
        set
        {
            pixelsPerSecond = Math.Clamp(value, 1, 500);
            InvalidateVisual();

            ZoomChanged?.Invoke(pixelsPerSecond);
        }
    }

    public void SetSegmentFocus(uint? segmentId)
    {
        focusedSegmentId = segmentId;
        auditionSegmentId = segmentId;
        verticalOffset = 0;
        SelectedTrackId = VisibleTracks.FirstOrDefault(track => track.SegmentObjectId == segmentId)?.Id
            ?? VisibleTracks.FirstOrDefault()?.Id;
        SelectedClipId = null;
        SelectedMediaId = null;
        UpdateTrackNameLabels();
        InvalidateVisual();
        NotifyBpmEditorPlacement();

        SelectionChanged?.Invoke();
    }

    public TimelineViewState CaptureViewState() => new(
        PixelsPerSecond,
        horizontalOffsetMs,
        verticalOffset,
        headerWidth,
        PlayheadMs,
        SelectionStartMs,
        SelectionEndMs,
        focusedSegmentId,
        auditionSegmentId,
        SelectedTrackId,
        SelectedClipId,
        SelectedMediaId);

    public void RestoreViewState(TimelineViewState state)
    {
        pixelsPerSecond = Math.Clamp(state.PixelsPerSecond, 1, 500);
        horizontalOffsetMs = Math.Max(0, state.HorizontalOffsetMs);
        verticalOffset = Math.Max(0, state.VerticalOffset);
        headerWidth = Math.Clamp(state.HeaderWidth, MinimumHeaderWidth, MaximumHeaderWidth());
        userSizedHeader = true;
        PlayheadMs = Math.Max(0, state.PlayheadMs);
        SelectionStartMs = state.SelectionStartMs;
        SelectionEndMs = state.SelectionEndMs;
        focusedSegmentId = state.FocusedSegmentId;
        auditionSegmentId = state.AuditionSegmentId;
        SelectedTrackId = state.SelectedTrackId;
        SelectedClipId = state.SelectedClipId;
        SelectedMediaId = state.SelectedMediaId;

        EnsureSelectionExists();
        UpdateTrackNameLabels();
        InvalidateArrange();
        InvalidateVisual();
        NotifyBpmEditorPlacement();

        SelectionChanged?.Invoke();
        ZoomChanged?.Invoke(pixelsPerSecond);
    }

    public void SetMetronomeSegments(IEnumerable<uint> segmentIds)
    {
        metronomeSegments = segmentIds.ToHashSet();
        InvalidateVisual();
    }

    public void SetNonAudioMediaIds(IEnumerable<uint> mediaIds)
    {
        nonAudioMediaIds = mediaIds.ToHashSet();
        InvalidateVisual();
    }

    public void SetDirtyTracks(IEnumerable<Guid> trackIds)
    {
        dirtyTrackIds = trackIds.ToHashSet();
        UpdateTrackNameLabels();
        InvalidateVisual();
    }

    public void SetAudioColorScope(string? scope)
    {
        audioColorScope = scope;
        InvalidateVisual();
    }

    public void SetSourceEvent(string? eventName)
    {
        sourceEventName = eventName;
        InvalidateVisual();
    }

    public void SetVisibleSegments(IEnumerable<uint>? segmentIds)
    {
        visibleSegmentIds = segmentIds?.ToHashSet();
        if (visibleSegmentIds is { Count: 0 })
        {
            visibleSegmentIds = null;
        }

        verticalOffset = 0;
        EnsureSelectionExists();
        if (SelectedTrackId is null || VisibleTracks.All(track => track.Id != SelectedTrackId))
        {
            SelectedTrackId = VisibleTracks.FirstOrDefault()?.Id;
            SelectedClipId = null;
            SelectedMediaId = null;
        }

        if (VisibleTracks.FirstOrDefault(track => track.Id == SelectedTrackId) is { } selectedTrack)
        {
            auditionSegmentId = selectedTrack.SegmentObjectId;
        }

        SyncTrackNameLabels();
        InvalidateArrange();
        InvalidateVisual();
        NotifyBpmEditorPlacement();

        SelectionChanged?.Invoke();
    }

    public void FitToWidth()
    {
        var tracks = VisibleTracks;
        var end = tracks.SelectMany(track => track.Clips)
            .Select(clip => clip.StartMs + clip.DurationMs)
            .Append(tracks.Select(track => track.LengthMs ?? 0).DefaultIfEmpty().Max())
            .DefaultIfEmpty(document.TimelineLengthMs ?? 1)
            .Max();

        end = Math.Max(1, end);
        horizontalOffsetMs = 0;
        PixelsPerSecond = Math.Clamp((Bounds.Width - headerWidth - 18) * 1_000 / end, 1, 500);
    }

    public void SetWaveform(MusicTimelineClip clip, WaveformEnvelope envelope)
    {
        waveforms[AudioKey(clip)] = envelope;
        InvalidateVisual();
    }

    public void ClearWaveforms()
    {
        waveforms.Clear();
        InvalidateVisual();
    }

    public void SetPlaybackPosition(double positionMs, bool follow = false)
    {
        PlayheadMs = Math.Max(0, positionMs);
        if (follow && Bounds.Width > headerWidth)
        {
            var visibleDuration = (Bounds.Width - headerWidth) * 1_000 / PixelsPerSecond;
            var visibleStart = horizontalOffsetMs;
            var visibleEnd = visibleStart + visibleDuration;

            if (PlayheadMs < visibleStart || PlayheadMs > visibleEnd - visibleDuration * 0.08)
            {
                horizontalOffsetMs = Math.Max(0, PlayheadMs - visibleDuration * 0.15);
            }
        }

        InvalidateVisual();
    }

    public void FocusPlayhead()
    {
        CenterViewOnPlayhead();
        StatusChanged?.Invoke($"Focused playhead at {FormatTime(PlayheadMs)}");
    }

    private void CenterViewOnPlayhead()
    {
        if (Bounds.Width <= headerWidth)
        {
            return;
        }

        var visibleDuration = (Bounds.Width - headerWidth) * 1_000 / PixelsPerSecond;
        horizontalOffsetMs = Math.Max(0, PlayheadMs - visibleDuration / 2);
        InvalidateVisual();
    }

    private void RenderTimeline(DrawingContext context)
    {
        context.FillRectangle(Brush("#101318"), Bounds);
        DrawGrid(context);
        DrawTracks(context);
        DrawSelection(context);
        DrawMarkersAndBoundary(context);
        DrawPlayhead(context);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        renderer.Measure(availableSize);
        foreach (var label in trackNameLabels.Values)
        {
            label.Measure(new Size(Math.Max(0, headerWidth - 24), 24));
        }

        return new Size();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        renderer.Arrange(new Rect(finalSize));
        var layouts = TrackLayouts().ToDictionary(row => row.Track.Id);
        foreach (var item in trackNameLabels)
        {
            if (!layouts.TryGetValue(item.Key, out var row)
                || row.Top + 30 < RulerHeight
                || row.Top > finalSize.Height)
            {
                item.Value.Arrange(new Rect());
                continue;
            }

            item.Value.Arrange(new Rect(12, row.Top + 8, Math.Max(0, headerWidth - 24), 24));
        }

        return finalSize;
    }

    private new void InvalidateVisual()
    {
        base.InvalidateVisual();
        renderer.InvalidateVisual();
    }

    public void DeleteSelected()
    {
        if (SelectedClipId is not { } id)
        {
            return;
        }

        document.RemoveClip(id);
        SelectedClipId = null;
        SelectedMediaId = null;

        SelectionChanged?.Invoke();
        StatusChanged?.Invoke("Clip removed");
    }

    public void SelectClip(Guid clipId)
    {
        var (track, clip) = document.FindClip(clipId);
        SelectedTrackId = track.Id;
        SelectedClipId = clip.Id;
        SelectedMediaId = clip.MediaId;
        SelectAuditionSegment(track);

        UpdateTrackNameLabels();
        SelectionChanged?.Invoke();
        InvalidateVisual();
        NotifyBpmEditorPlacement();
    }

    public void SplitSelected()
    {
        if (SelectedClipId is not { } id)
        {
            StatusChanged?.Invoke("Select a clip before splitting");
            return;
        }

        try
        {
            SelectedClipId = document.SplitClip(id, PlayheadMs);

            SelectionChanged?.Invoke();
            StatusChanged?.Invoke("Clip split at playhead");
        }
        catch (InvalidOperationException exception)
        {
            StatusChanged?.Invoke(exception.Message);
        }
    }

    public void DuplicateSelected()
    {
        if (SelectedClipId is not { } id)
        {
            StatusChanged?.Invoke("Select a clip before duplicating it");
            return;
        }

        SelectedClipId = document.DuplicateClip(id);
        var (track, clip) = document.FindClip(SelectedClipId.Value);
        SelectedTrackId = track.Id;
        SelectedMediaId = clip.MediaId;

        SelectionChanged?.Invoke();
        StatusChanged?.Invoke($"Duplicated {clip.Name} at {FormatTime(clip.StartMs)}");
    }

    public void MovePlayheadToStart()
    {
        SelectionStartMs = 0;
        SelectionEndMs = 0;
        SetPlaybackPosition(0);
        CenterViewOnPlayhead();

        SeekRequested?.Invoke(0);
        SelectionChanged?.Invoke();
        StatusChanged?.Invoke("Playhead moved to timeline start");
    }

    public void Undo()
    {
        document.Undo();
        EnsureSelectionExists();

        SelectionChanged?.Invoke();
    }

    public void Redo()
    {
        document.Redo();
        EnsureSelectionExists();

        SelectionChanged?.Invoke();
    }

    public void RemoveSelectedTrack()
    {
        if (SelectedTrackId is not { } trackId)
        {
            StatusChanged?.Invoke("Select a track before removing it");
            return;
        }

        var oldTracks = VisibleTracks.ToArray();
        var removedIndex = Array.FindIndex(oldTracks, track => track.Id == trackId);
        var selectedClipWasRemoved = oldTracks.FirstOrDefault(track => track.Id == trackId)?.Clips
            .Any(clip => clip.Id == SelectedClipId) == true;

        document.RemoveTrack(trackId);
        var remaining = VisibleTracks;
        SelectedTrackId = remaining.Count == 0
            ? null
            : remaining[Math.Clamp(removedIndex, 0, remaining.Count - 1)].Id;
        if (selectedClipWasRemoved)
        {
            SelectedClipId = null;
            SelectedMediaId = null;
        }

        UpdateTrackNameLabels();
        SelectionChanged?.Invoke();
        NotifyBpmEditorPlacement();

        TrackMixChanged?.Invoke();
        StatusChanged?.Invoke("Selected track removed");
    }

    public Guid AddTrackBelowSelected()
    {
        var selected = SelectedTrackId is { } selectedId
            ? document.Tracks.FirstOrDefault(track => track.Id == selectedId)
            : document.Tracks.FirstOrDefault(track => track.SegmentObjectId == AuditionSegmentId);
        var id = document.InsertTrackAfter(
            selected?.Id,
            $"New track {document.Tracks.Count + 1}",
            selected?.ObjectId,
            selected?.SegmentObjectId ?? AuditionSegmentId,
            selected?.LengthMs);

        SelectedTrackId = id;
        SelectedClipId = null;
        SelectedMediaId = null;

        UpdateTrackNameLabels();
        SelectionChanged?.Invoke();
        NotifyBpmEditorPlacement();

        StatusChanged?.Invoke(selected is null
            ? "Track added"
            : $"Track added below {TrackDisplayName(selected)}");
        return id;
    }

    public bool SelectForContext(Point position)
    {
        var layout = TrackLayoutAt(position.Y);
        if (layout is null)
        {
            return false;
        }

        SelectedTrackId = layout.Track.Id;
        var hit = position.X >= headerWidth ? HitTest(position) : null;
        if (hit is { } selected && selected.Track.Id == layout.Track.Id)
        {
            SelectedClipId = selected.Clip.Id;
            SelectedMediaId = selected.Clip.MediaId;
        }
        else
        {
            SelectedClipId = null;
            SelectedMediaId = null;
        }

        var auditionChanged = SelectAuditionSegment(layout.Track);

        UpdateTrackNameLabels();
        SelectionChanged?.Invoke();
        if (auditionChanged)
        {
            AuditionSegmentChanged?.Invoke();
        }

        InvalidateVisual();
        NotifyBpmEditorPlacement();

        return true;
    }

    public void ToggleSelectedTrackMuted()
    {
        if (SelectedTrackId is not { } trackId
            || document.Tracks.FirstOrDefault(track => track.Id == trackId) is not { } track)
        {
            StatusChanged?.Invoke("Select a track before changing its mute state");
            return;
        }

        var muted = !track.IsMuted;
        document.SetTrackMuted(trackId, muted);

        TrackMixChanged?.Invoke();
        SelectionChanged?.Invoke();
        StatusChanged?.Invoke(muted ? "Track muted" : "Track unmuted");
    }

    public void ToggleSelectedTrackSolo()
    {
        if (SelectedTrackId is not { } trackId
            || document.Tracks.FirstOrDefault(track => track.Id == trackId) is not { } track)
        {
            StatusChanged?.Invoke("Select a track before changing its solo state");
            return;
        }

        var solo = !track.IsSolo;
        document.SetTrackSolo(trackId, solo);

        TrackMixChanged?.Invoke();
        SelectionChanged?.Invoke();
        StatusChanged?.Invoke(solo ? "Track isolated" : "Track isolation cleared");
    }

    public Guid? ClipAt(Point point) => point.X >= headerWidth ? HitTest(point)?.Clip.Id : null;

    public uint? SegmentAt(Point? point = null)
    {
        if (point is not { } position)
        {
            return AuditionSegmentId;
        }

        return SegmentLayouts().FirstOrDefault(segment =>
            position.Y >= segment.Top && position.Y < segment.Bottom)?.SegmentId ?? AuditionSegmentId;
    }

    public Guid? TrackAt(Point? dropPoint = null)
    {
        var trackId = SelectedTrackId;
        if (dropPoint is { } point)
        {
            var layout = TrackLayoutAt(point.Y, clamp: true);
            if (layout is not null && point.Y >= RulerHeight)
            {
                trackId = layout.Track.Id;
            }
        }

        return trackId;
    }

    public Guid AddExternalClip(
        string name,
        string path,
        double durationMs,
        Point? dropPoint = null,
        uint? mediaId = null,
        int? sourceIdOffset = null,
        uint? replacementMediaId = null,
        BnkTimelineFieldOffsets? fieldOffsets = null,
        int? playlistIndex = null,
        bool createNewTrack = false,
        uint? trackObjectId = null,
        uint? segmentObjectId = null,
        double? trackLengthMs = null)
    {
        var trackId = createNewTrack ? null : TrackAt(dropPoint);
        var targetSegmentId = segmentObjectId ?? (trackId is { } selectedTrackId
            ? document.Tracks.FirstOrDefault(track => track.Id == selectedTrackId)?.SegmentObjectId
            : SegmentAt(dropPoint));
        var startMs = dropPoint is { } point
            ? document.Snap(TimeAt(Math.Max(headerWidth, point.X)), targetSegmentId)
            : PlayheadMs;

        trackId ??= document.AddTrack(name, trackObjectId, targetSegmentId, trackLengthMs);
        var clipId = document.AddClip(
            trackId.Value,
            name,
            startMs,
            durationMs,
            mediaId,
            sourcePath: path,
            physicalDurationMs: durationMs,
            sourceIdOffset: sourceIdOffset,
            replacementMediaId: replacementMediaId,
            fieldOffsets: fieldOffsets,
            playlistIndex: playlistIndex);
        SelectedTrackId = trackId;
        SelectedClipId = clipId;
        SelectedMediaId = null;
        PlayheadMs = startMs;

        SelectionChanged?.Invoke();
        InvalidateVisual();

        return clipId;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Source is Visual source
            && source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is { Tag: Guid trackId })
        {
            SelectTrackLabel(trackId);
            return;
        }

        Focus();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        if (position.Y >= RulerHeight && Math.Abs(position.X - headerWidth) <= SeparatorWidth / 2)
        {
            resizingHeader = true;
            userSizedHeader = true;
            e.Pointer.Capture(this);
            e.Handled = true;

            return;
        }

        if (position.X >= headerWidth && position.Y < RulerHeight)
        {
            SetPlaybackPosition(SnapPlayhead(TimeAt(position.X), AuditionSegmentId));

            SeekRequested?.Invoke(PlayheadMs);
            StatusChanged?.Invoke($"Playhead {FormatTime(PlayheadMs)}");
            e.Handled = true;

            return;
        }

        if (ShowMarkers && MarkerAt(position) is { } marker)
        {
            var selectedTrack = VisibleTracks.FirstOrDefault(track => track.Id == SelectedTrackId);
            if (selectedTrack?.SegmentObjectId != marker.SegmentObjectId
                && VisibleTracks.FirstOrDefault(track =>
                    track.SegmentObjectId == marker.SegmentObjectId) is { } markerTrack)
            {
                SelectedTrackId = markerTrack.Id;
                SelectedClipId = null;
                SelectedMediaId = null;
                UpdateTrackNameLabels();
            }

            var auditionChanged = SelectAuditionSegment(marker.SegmentObjectId);
            SetPlaybackPosition(marker.PositionMs);
            SelectionChanged?.Invoke();
            if (auditionChanged)
            {
                AuditionSegmentChanged?.Invoke();
            }

            SeekRequested?.Invoke(PlayheadMs);
            NotifyBpmEditorPlacement();
            markerDrag = new MarkerDragState(marker);
            previewMarkerPositionMs = marker.PositionMs;
            e.Pointer.Capture(this);

            StatusChanged?.Invoke($"Playhead snapped to {marker.Name}; drag to move the cue");
            e.Handled = true;

            return;
        }

        var segmentHeader = SegmentLayouts().FirstOrDefault(item =>
            position.Y >= item.Top && position.Y < item.TrackTop);
        if (segmentHeader is not null)
        {
            var editBpm = BpmEditorBounds(segmentHeader).Contains(position);
            var auditionChanged = segmentHeader.SegmentId != auditionSegmentId;

            auditionSegmentId = segmentHeader.SegmentId;
            SelectedTrackId = segmentHeader.Tracks.FirstOrDefault()?.Id;
            SelectedClipId = null;
            SelectedMediaId = null;
            UpdateTrackNameLabels();
            if (position.X >= headerWidth && !editBpm)
            {
                SetPlaybackPosition(SnapPlayhead(TimeAt(position.X), segmentHeader.SegmentId));

                SeekRequested?.Invoke(PlayheadMs);
            }

            SelectionChanged?.Invoke();
            if (auditionChanged)
            {
                AuditionSegmentChanged?.Invoke();
            }

            NotifyBpmEditorPlacement();
            if (editBpm && segmentHeader.SegmentId is not null)
            {
                SegmentBpmEditRequested?.Invoke(segmentHeader.SegmentId.Value);
            }

            StatusChanged?.Invoke($"Auditioning {SegmentTitle(segmentHeader)}");
            InvalidateVisual();
            e.Handled = true;

            return;
        }

        if (position.X < headerWidth && position.Y >= RulerHeight)
        {
            var trackLayout = TrackLayoutAt(position.Y);
            if (trackLayout is not null)
            {
                var track = trackLayout.Track;
                SelectedTrackId = track.Id;
                SelectedClipId = null;
                SelectedMediaId = null;

                var auditionChanged = SelectAuditionSegment(track);
                var mixChanged = false;
                var top = trackLayout.Top;

                if (!standaloneAudioMode && MuteBounds(top).Contains(position))
                {
                    document.SetTrackMuted(track.Id, !track.IsMuted);

                    StatusChanged?.Invoke(track.IsMuted ? "Track unmuted" : "Track muted");
                    TrackMixChanged?.Invoke();
                    mixChanged = true;
                }
                else if (!standaloneAudioMode && SoloBounds(top).Contains(position))
                {
                    document.SetTrackSolo(track.Id, !track.IsSolo);

                    StatusChanged?.Invoke(track.IsSolo ? "Track solo cleared" : "Track isolated");
                    TrackMixChanged?.Invoke();
                    mixChanged = true;
                }
                else if (GainBounds(top).Contains(position))
                {
                    gainDragTrackId = track.Id;
                    SetTrackGainFromPointer(track, position.X, top);
                    e.Pointer.Capture(this);
                }

                if (auditionChanged && !mixChanged)
                {
                    AuditionSegmentChanged?.Invoke();
                }

                SelectionChanged?.Invoke();
                UpdateTrackNameLabels();
                InvalidateVisual();
                NotifyBpmEditorPlacement();
            }

            e.Handled = true;
            return;
        }

        var hit = HitTest(position);
        if (hit is not null)
        {
            SelectedClipId = hit.Value.Clip.Id;
            SelectedMediaId = hit.Value.Clip.MediaId;
            SelectedTrackId = hit.Value.Track.Id;

            var auditionChanged = SelectAuditionSegment(hit.Value.Track);
            UpdateTrackNameLabels();
            SetPlaybackPosition(SnapPlayhead(TimeAt(position.X), hit.Value.Track.SegmentObjectId));

            SeekRequested?.Invoke(PlayheadMs);
            SelectionChanged?.Invoke();
            if (auditionChanged)
            {
                AuditionSegmentChanged?.Invoke();
            }

            NotifyBpmEditorPlacement();

            var trimStart = position.X - hit.Value.Bounds.Left <= HandleWidth;
            var trimEnd = hit.Value.Bounds.Right - position.X <= HandleWidth;

            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)
                || e.KeyModifiers.HasFlag(KeyModifiers.Control) && (trimStart || trimEnd))
            {
                var mode = e.KeyModifiers.HasFlag(KeyModifiers.Alt)
                    ? DragMode.Move
                    : trimStart ? DragMode.TrimStart : DragMode.TrimEnd;
                drag = new DragState(position, hit.Value.Clip, mode);
                previewClip = hit.Value.Clip;
                previewTrackId = hit.Value.Track.Id;

                StatusChanged?.Invoke(mode == DragMode.Move
                    ? "Moving clip - release Alt+drag to place it"
                    : "Trimming clip - release Ctrl+drag to apply");
            }
        }
        else
        {
            var target = TrackLayoutAt(position.Y);
            var auditionChanged = false;
            if (target is not null)
            {
                SelectedTrackId = target.Track.Id;
                SelectedClipId = null;
                SelectedMediaId = null;
                auditionChanged = SelectAuditionSegment(target.Track);
                UpdateTrackNameLabels();
                NotifyBpmEditorPlacement();
            }

            SetPlaybackPosition(SnapPlayhead(TimeAt(position.X), target?.SegmentId ?? AuditionSegmentId));
            SelectionChanged?.Invoke();
            if (auditionChanged)
            {
                AuditionSegmentChanged?.Invoke();
            }

            SeekRequested?.Invoke(PlayheadMs);
            StatusChanged?.Invoke($"Playhead {FormatTime(PlayheadMs)}");
        }

        if (drag is null && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            var segmentId = hit?.Track.SegmentObjectId ?? TrackLayoutAt(position.Y)?.SegmentId ?? AuditionSegmentId;
            var anchor = document.SnapNear(
                TimeAt(position.X),
                8 * 1_000 / PixelsPerSecond,
                segmentId,
                VisibleGridMilliseconds(segmentId));

            selectionDrag = new SelectionDragState(anchor, segmentId);
            SelectionStartMs = anchor;
            SelectionEndMs = anchor;
        }
        else if (drag is null)
        {
            SelectionStartMs = null;
            SelectionEndMs = null;
            SelectionChanged?.Invoke();
        }

        if (drag is not null || selectionDrag is not null)
        {
            e.Pointer.Capture(this);
        }

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (resizingHeader)
        {
            headerWidth = Math.Clamp(e.GetPosition(this).X, MinimumHeaderWidth, MaximumHeaderWidth());
            InvalidateArrange();
            InvalidateVisual();
            NotifyBpmEditorPlacement();
            e.Handled = true;

            return;
        }

        if (gainDragTrackId is { } gainTrackId)
        {
            var layout = TrackLayouts().FirstOrDefault(item => item.Track.Id == gainTrackId);
            if (layout is not null)
            {
                SetTrackGainFromPointer(layout.Track, e.GetPosition(this).X, layout.Top);
            }

            e.Handled = true;
            return;
        }

        if (markerDrag is not null)
        {
            previewMarkerPositionMs = document.SnapNear(
                TimeAt(e.GetPosition(this).X),
                8 * 1_000 / PixelsPerSecond,
                markerDrag.Marker.SegmentObjectId,
                VisibleGridMilliseconds(markerDrag.Marker.SegmentObjectId));
            InvalidateVisual();
            e.Handled = true;

            return;
        }

        if (selectionDrag is not null)
        {
            var edge = document.SnapNear(
                TimeAt(e.GetPosition(this).X),
                8 * 1_000 / PixelsPerSecond,
                selectionDrag.SegmentId,
                VisibleGridMilliseconds(selectionDrag.SegmentId));
            SelectionStartMs = Math.Min(selectionDrag.AnchorMs, edge);
            SelectionEndMs = Math.Max(selectionDrag.AnchorMs, edge);
            InvalidateVisual();
            e.Handled = true;

            return;
        }

        if (drag is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var deltaMs = (position.X - drag.StartPoint.X) * 1000 / PixelsPerSecond;
        var edgeToleranceMs = 8 * 1000 / PixelsPerSecond;

        if (drag.Mode == DragMode.Move)
        {
            var target = TrackLayoutAt(position.Y, clamp: true);
            if (target is null)
            {
                return;
            }

            previewTrackId = target.Track.Id;
            previewClip = document.ConstrainMove(drag.Clip.Id, previewTrackId.Value,
                drag.Clip.StartMs + deltaMs, edgeToleranceMs);
        }
        else if (drag.Mode == DragMode.TrimStart)
        {
            var end = drag.Clip.StartMs + drag.Clip.DurationMs;
            var earliest = Math.Max(0, drag.Clip.StartMs - drag.Clip.SourceOffsetMs);
            var start = Math.Clamp(drag.Clip.StartMs + deltaMs, earliest, end - 1);
            var change = start - drag.Clip.StartMs;

            previewClip = document.ConstrainResize(drag.Clip.Id, start,
                drag.Clip.SourceOffsetMs + change, end - start, edgeToleranceMs);
        }
        else
        {
            var end = Math.Max(drag.Clip.StartMs + 1, drag.Clip.StartMs + drag.Clip.DurationMs + deltaMs);
            previewClip = document.ConstrainResize(drag.Clip.Id, drag.Clip.StartMs,
                drag.Clip.SourceOffsetMs, end - drag.Clip.StartMs, edgeToleranceMs);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (resizingHeader)
        {
            resizingHeader = false;
            e.Pointer.Capture(null);
            e.Handled = true;

            return;
        }

        if (gainDragTrackId is not null)
        {
            gainDragTrackId = null;
            e.Pointer.Capture(null);
            e.Handled = true;

            return;
        }

        if (markerDrag is { } movingMarker && previewMarkerPositionMs is { } markerPosition)
        {
            document.SetMarkerPosition(movingMarker.Marker, markerPosition, snapToleranceMs: 0);
            markerDrag = null;
            previewMarkerPositionMs = null;
            e.Pointer.Capture(null);

            StatusChanged?.Invoke($"Moved {movingMarker.Marker.Name} to {FormatTime(markerPosition)}");
            e.Handled = true;

            return;
        }

        if (selectionDrag is not null)
        {
            selectionDrag = null;
            e.Pointer.Capture(null);

            SelectionChanged?.Invoke();
            StatusChanged?.Invoke(SelectionEndMs - SelectionStartMs > 1
                ? $"Selected {FormatTime(SelectionStartMs ?? 0)} - {FormatTime(SelectionEndMs ?? 0)}"
                : $"Cursor {FormatTime(SelectionStartMs ?? 0)}");
            e.Handled = true;

            return;
        }

        if (drag is null || previewClip is null || previewTrackId is null)
        {
            return;
        }

        if (drag.Mode == DragMode.Move)
        {
            document.MoveClip(drag.Clip.Id, previewTrackId.Value, previewClip.StartMs,
                8 * 1000 / PixelsPerSecond);
        }
        else
        {
            document.ResizeClip(drag.Clip.Id, previewClip.StartMs, previewClip.SourceOffsetMs,
                previewClip.DurationMs, 8 * 1000 / PixelsPerSecond);
        }

        SelectedTrackId = previewTrackId.Value;

        StatusChanged?.Invoke(drag.Mode == DragMode.Move ? "Clip moved" : "Clip trimmed");
        drag = null;
        previewClip = null;
        previewTrackId = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var position = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var anchor = TimeAt(position.X);
            PixelsPerSecond *= e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
            horizontalOffsetMs = Math.Max(0, anchor - (position.X - headerWidth) * 1000 / PixelsPerSecond);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || MaxVerticalOffset() == 0)
        {
            horizontalOffsetMs = Math.Max(0, horizontalOffsetMs - e.Delta.Y * 1_500);
        }
        else
        {
            verticalOffset = Math.Clamp(verticalOffset - e.Delta.Y * TrackHeight, 0, MaxVerticalOffset());
            InvalidateArrange();
        }

        InvalidateVisual();
        NotifyBpmEditorPlacement();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.S)
        {
            SplitSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            DuplicateSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.None)
        {
            MovePlayheadToStart();
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.None)
        {
            FocusPlayhead();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            PlayPauseRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        context.FillRectangle(Brush("#181D24"), new Rect(0, 0, Bounds.Width, RulerHeight));
        context.FillRectangle(Brush("#151A20"), new Rect(0, RulerHeight, headerWidth, Bounds.Height - RulerHeight));
        if (standaloneAudioMode)
        {
            DrawTimeGrid(context, 0, RulerHeight, drawLabels: true);
        }
        else
        {
            var segmentId = AuditionSegmentId;
            var beatMs = document.BeatMillisecondsFor(segmentId);
            var step = document.GridMillisecondsFor(segmentId);

            while (step * PixelsPerSecond / 1000 < 8)
            {
                step *= 2;
            }

            var first = Math.Floor(horizontalOffsetMs / step) * step;
            var visibleEnd = TimeAt(Bounds.Width);
            var lastLabelX = double.NegativeInfinity;

            for (var time = first; time <= visibleEnd; time += step)
            {
                var beat = (long)Math.Round(time / beatMs);
                var bar = beat % document.BeatsPerBar == 0;
                var x = XAt(time);

                context.DrawLine(new Pen(Brush(bar ? "#536070" : "#2A3039"), bar ? 1.2 : 0.7),
                    new Point(x, bar ? 0 : 18), new Point(x, RulerHeight));
                if (bar && x >= headerWidth && x - lastLabelX >= 54)
                {
                    DrawText(context, $"{beat / document.BeatsPerBar + 1}", x + 4, 7, 12, "#C3CBD5");
                    lastLabelX = x;
                }
            }
        }

        context.DrawLine(new Pen(Brush("#3B424C")), new Point(0, RulerHeight), new Point(Bounds.Width, RulerHeight));
        context.FillRectangle(Brush("#46515E"),
            new Rect(headerWidth - SeparatorWidth / 2, RulerHeight, SeparatorWidth, Bounds.Height - RulerHeight));
        context.DrawLine(new Pen(Brush("#A5B0BC"), 2), new Point(headerWidth, RulerHeight), new Point(headerWidth, Bounds.Height));
        for (var y = RulerHeight + 15; y < Bounds.Height - 4; y += 24)
        {
            context.FillRectangle(Brush("#CAD2DA"), new Rect(headerWidth - 3.5, y, 3, 3), 1.5f);
            context.FillRectangle(Brush("#CAD2DA"), new Rect(headerWidth + 1.5, y, 3, 3), 1.5f);
        }
    }

    private void DrawTracks(DrawingContext context)
    {
        using var clipState = context.PushClip(new Rect(0, RulerHeight, Bounds.Width, Bounds.Height - RulerHeight));
        var segments = SegmentLayouts();
        if (segments.Count == 0)
        {
            DrawText(context, "Select a playable Music Segment in the browser tree to open its Tracks and Clips.",
                headerWidth + 28, RulerHeight + 30, 13, "#778392", Math.Max(0, Bounds.Width - headerWidth - 56));
            return;
        }

        var rowIndex = 0;
        foreach (var segment in segments)
        {
            if (segment.Bottom < RulerHeight || segment.Top > Bounds.Height)
            {
                rowIndex += segment.TrackRows.Count;
                continue;
            }

            if (!standaloneAudioMode)
            {
                DrawSegmentHeader(context, segment);
            }

            var anySolo = !standaloneAudioMode && segment.Tracks.Any(track => track.IsSolo);
            if (segment.LengthMs is { } segmentLength)
            {
                var end = XAt(segmentLength);
                if (end < Bounds.Width)
                {
                    var left = Math.Max(headerWidth, end);
                    context.FillRectangle(Brush("#301C1217"),
                        new Rect(left, segment.TrackTop, Math.Max(0, Bounds.Width - left), segment.Bottom - segment.TrackTop));
                }

                if (end >= headerWidth && end <= Bounds.Width)
                {
                    context.DrawLine(new Pen(Brush("#C97667"), 1.4),
                        new Point(end, segment.TrackTop), new Point(end, segment.Bottom));
                }
            }

            foreach (var row in segment.TrackRows)
            {
                if (row.Top + TrackHeight < RulerHeight || row.Top > Bounds.Height)
                {
                    rowIndex++;
                    continue;
                }

                var track = row.Track;
                var top = row.Top;
                var audible = standaloneAudioMode || (anySolo ? track.IsSolo : !track.IsMuted);
                var selectedSegment = !standaloneAudioMode && segment.SegmentId == AuditionSegmentId;

                context.FillRectangle(Brush(selectedSegment
                        ? rowIndex++ % 2 == 0 ? "#211B2D" : "#251E32"
                        : rowIndex++ % 2 == 0 ? "#11151B" : "#131820"),
                    new Rect(headerWidth, top, Math.Max(0, Bounds.Width - headerWidth), TrackHeight));
                if (standaloneAudioMode)
                {
                    DrawTimeGrid(context, top, TrackHeight, drawLabels: false);
                }
                else
                {
                    DrawSegmentGrid(context, segment.SegmentId, top, TrackHeight);
                }
                if (segment.SegmentId == AuditionSegmentId)
                {
                    context.FillRectangle(Brush("#242033"), new Rect(0, top, headerWidth, TrackHeight));
                }

                if (dirtyTrackIds.Contains(track.Id))
                {
                    context.FillRectangle(Brush("#44311E"), new Rect(0, top, headerWidth, TrackHeight));
                    context.FillRectangle(Brush("#F0A84F"), new Rect(0, top, 4, TrackHeight));
                }

                if (track.Id == SelectedTrackId)
                {
                    context.FillRectangle(Brush("#173C3A"), new Rect(0, top, headerWidth, TrackHeight));
                    context.DrawRectangle(new Pen(Brush("#4ECDC4"), 2),
                        new Rect(2, top + 2, Math.Max(0, Bounds.Width - 4), TrackHeight - 4));
                }

                if (!audible)
                {
                    context.FillRectangle(Brush("#7A090B0E"), new Rect(0, top, Bounds.Width, TrackHeight));
                }

                context.DrawLine(new Pen(Brush("#303741")),
                    new Point(0, top + TrackHeight), new Point(Bounds.Width, top + TrackHeight));
                var trackIdentity = standaloneAudioMode
                    ? "Sound editor"
                    : track.ObjectId is null ? "Manual track" : $"Track {track.ObjectId}";
                DrawText(context,
                    $"{trackIdentity} ({track.Clips.Length} clip{(track.Clips.Length == 1 ? string.Empty : "s")})",
                    12,
                    top + 36,
                    11,
                    "#778392",
                    headerWidth - 24);
                DrawTrackControls(context, track, top);
                foreach (var clip in track.Clips)
                {
                    if (drag?.Clip.Id != clip.Id)
                    {
                        DrawClip(context, track, clip, top);
                    }
                }
            }

            if (!standaloneAudioMode && segment.SegmentId == AuditionSegmentId)
            {
                context.DrawRectangle(new Pen(Brush("#A78BFA"), 2),
                    new Rect(1, segment.Top + 1, Math.Max(0, Bounds.Width - 2), segment.Bottom - segment.Top - 2));
            }
        }

        if (previewClip is not null && previewTrackId is not null)
        {
            var row = TrackLayouts().FirstOrDefault(item => item.Track.Id == previewTrackId);
            if (row is not null)
            {
                DrawClip(context, row.Track, previewClip, row.Top, true);
            }
        }
    }

    private void DrawSegmentHeader(DrawingContext context, SegmentLayout segment)
    {
        var selected = segment.SegmentId == AuditionSegmentId;
        context.FillRectangle(Brush(selected ? "#352A4A" : "#202731"),
            new Rect(0, segment.Top, Bounds.Width, SegmentHeaderHeight));
        context.FillRectangle(Brush(selected ? "#A78BFA" : "#56616E"),
            new Rect(0, segment.Top, 4, SegmentHeaderHeight));
        context.DrawLine(new Pen(Brush("#3C4652")),
            new Point(0, segment.TrackTop), new Point(Bounds.Width, segment.TrackTop));
        DrawText(context, segment.Name, 12, segment.Top + 6, 13, selected ? "#F4F8FC" : "#D6DCE3", headerWidth - 24);

        var identity = segment.SegmentId is { } id
            ? $"Music Segment {id}"
                + (string.IsNullOrWhiteSpace(sourceEventName) ? string.Empty : $"  |  Event {sourceEventName}")
            : "Manual arrangement";
        var duration = segment.LengthMs is { } length ? $"  |  {FormatTime(length)}" : string.Empty;
        var bpm = document.SegmentBpm(segment.SegmentId);

        DrawText(context,
            $"{identity}  |  {segment.Tracks.Count} track{(segment.Tracks.Count == 1 ? string.Empty : "s")}{duration}",
            12,
            segment.Top + 26,
            10,
            "#8F9BA8",
            headerWidth - 24);
        DrawText(context, "BPM", headerWidth + 12, segment.Top + 15, 10,
            selected ? "#D9CBFF" : "#8996A4");
        if (segment.SegmentId is not null && !selected)
        {
            var bounds = BpmEditorBounds(segment);
            context.FillRectangle(Brush("#18212A"), bounds, 3);
            context.DrawRectangle(new Pen(Brush("#4E5D6B")), bounds, 3);
            DrawText(context, bpm.ToString("0.###", CultureInfo.InvariantCulture),
                bounds.Left + 7, bounds.Top + 5, 12, "#D9E2E9", bounds.Width - 14);
        }

    }

    private void DrawSegmentGrid(DrawingContext context, uint? segmentId, double top, double height)
    {
        var beatMs = document.BeatMillisecondsFor(segmentId);
        var step = VisibleGridMilliseconds(segmentId);

        var first = Math.Floor(horizontalOffsetMs / step) * step;
        var visibleEnd = TimeAt(Bounds.Width);

        for (var time = first; time <= visibleEnd; time += step)
        {
            var beat = (long)Math.Round(time / beatMs);
            var bar = beat % document.BeatsPerBar == 0;
            var x = XAt(time);

            context.DrawLine(new Pen(Brush(bar ? "#394450" : "#232A32"), bar ? 1.1 : 0.7),
                new Point(x, top), new Point(x, top + height));
        }
    }

    private void DrawTimeGrid(DrawingContext context, double top, double height, bool drawLabels)
    {
        var candidates = new double[] { 10, 20, 50, 100, 200, 500, 1_000, 2_000, 5_000, 10_000, 30_000, 60_000 };
        var step = candidates.FirstOrDefault(value => value * PixelsPerSecond / 1000 >= (drawLabels ? 72 : 12));

        if (step <= 0)
        {
            step = 120_000;
        }

        var first = Math.Floor(horizontalOffsetMs / step) * step;
        var visibleEnd = TimeAt(Bounds.Width);

        for (var time = first; time <= visibleEnd; time += step)
        {
            var x = XAt(time);
            context.DrawLine(new Pen(Brush(drawLabels ? "#536070" : "#2C3540"), drawLabels ? 1.1 : 0.7),
                new Point(x, top), new Point(x, top + height));
            if (drawLabels && x >= headerWidth)
            {
                DrawText(context, FormatTime(time), x + 4, 7, 11, "#C3CBD5");
            }
        }
    }

    private double VisibleGridMilliseconds(uint? segmentId)
    {
        var step = document.GridMillisecondsFor(segmentId);
        while (step * PixelsPerSecond / 1_000 < 8)
        {
            step *= 2;
        }

        return step;
    }

    private double SnapPlayhead(double timeMs, uint? segmentId)
    {
        var value = Math.Max(0, timeMs);
        if (!document.SnapEnabled)
        {
            return value;
        }

        var feature = document.SnapNear(
            value,
            8 * 1_000 / PixelsPerSecond,
            segmentId,
            includeGrid: false);
        if (Math.Abs(feature - value) > 0.001)
        {
            return feature;
        }

        var step = VisibleGridMilliseconds(segmentId);
        return Math.Max(0, Math.Round(value / step, MidpointRounding.AwayFromZero) * step);
    }

    private void DrawClip(
        DrawingContext context,
        MusicTimelineTrack track,
        MusicTimelineClip clip,
        double trackTop,
        bool preview = false)
    {
        var left = XAt(clip.StartMs);
        var right = XAt(clip.StartMs + clip.DurationMs);

        if (right < headerWidth || left > Bounds.Width)
        {
            return;
        }

        using var contentClip = context.PushClip(new Rect(
            headerWidth,
            trackTop,
            Math.Max(0, Bounds.Width - headerWidth),
            TrackHeight));
        var rect = new Rect(left, trackTop + 8, Math.Max(2, right - left), TrackHeight - 16);
        var selected = clip.Id == SelectedClipId;
        var related = !selected && SelectedMediaId is not null && clip.MediaId == SelectedMediaId;
        var replaced = clip.MediaId is not null && clip.SourcePath is not null;
        var fit = MusicTimelineAnalysis.Analyze(track, clip);
        var fill = clip.MediaId is null ? "#263B43" : replaced ? "#43382C" : "#273744";
        var nameColor = NameColorPalette.Hex($"audio:{audioColorScope ?? track.ObjectId?.ToString(CultureInfo.InvariantCulture)}:{clip.Name}");

        context.FillRectangle(Brush(preview ? "#3D5963" : fill), rect, 4);
        var border = selected ? "#FFB45E"
            : related ? "#79C7F2"
            : fit.Severity == MusicClipFitSeverity.Error ? "#F06464"
            : fit.Severity == MusicClipFitSeverity.Warning ? "#E4A24C"
            : replaced ? "#D6A568"
            : "#566B7B";
        context.DrawRectangle(
            new Pen(Brush(border), selected || related || fit.Severity != MusicClipFitSeverity.Normal ? 2 : 1),
            rect,
            4);
        if (selected && rect.Width >= HandleWidth * 2)
        {
            context.FillRectangle(Brush("#B9C9D5"), new Rect(rect.Left, rect.Top, 3, rect.Height), 2);
            context.FillRectangle(Brush("#B9C9D5"), new Rect(rect.Right - 3, rect.Top, 3, rect.Height), 2);
        }

        var nonAudio = clip.MediaId is { } mediaId && nonAudioMediaIds.Contains(mediaId);
        if (nonAudio)
        {
            for (var stripe = rect.Left - rect.Height; stripe < rect.Right; stripe += 12)
            {
                context.DrawLine(new Pen(Brush("#53616B"), 1),
                    new Point(stripe, rect.Bottom), new Point(stripe + rect.Height, rect.Top));
            }
        }
        else
        {
            DrawWaveform(context, clip, rect, replaced);
        }

        DrawFades(context, clip, rect);
        if (clip.PhysicalDurationMs is > 0)
        {
            var sourceLeft = XAt(clip.StartMs - clip.SourceOffsetMs);
            var sourceRight = XAt(clip.StartMs - clip.SourceOffsetMs + clip.PhysicalDurationMs.Value);
            var visibleLeft = Math.Max(rect.Left, sourceLeft);
            var visibleRight = Math.Min(rect.Right, sourceRight);

            if (visibleRight > visibleLeft)
            {
                var y = rect.Bottom - 5;
                context.DrawLine(new Pen(Brush("#59616B"), 3),
                    new Point(visibleLeft, y), new Point(visibleRight, y));
                var usedLeft = Math.Max(rect.Left, XAt(clip.StartMs));
                var usedRight = Math.Min(rect.Right, XAt(clip.StartMs + fit.UsedPhysicalMs));

                if (usedRight > usedLeft)
                {
                    context.DrawLine(new Pen(Brush(replaced ? "#F0BE72" : "#A6C7E4"), 4),
                        new Point(usedLeft, y), new Point(usedRight, y));
                }

                if (sourceRight >= rect.Left && sourceRight <= rect.Right)
                {
                    context.DrawLine(new Pen(Brush("#E5EDF5"), 1),
                        new Point(sourceRight, y - 4), new Point(sourceRight, y + 3));
                }
            }

            if (fit.RepeatedMs > 1)
            {
                var repeatedLeft = Math.Max(rect.Left, XAt(clip.StartMs + fit.UsedPhysicalMs));
                if (rect.Right > repeatedLeft)
                {
                    context.FillRectangle(Brush("#554D321F"),
                        new Rect(repeatedLeft, rect.Top + 3, rect.Right - repeatedLeft, rect.Height - 6), 2);
                    for (var stripe = repeatedLeft + 3; stripe < rect.Right; stripe += 8)
                    {
                        context.DrawLine(new Pen(Brush("#B98B4F"), 1),
                            new Point(stripe, rect.Bottom - 11), new Point(Math.Min(rect.Right, stripe + 6), rect.Bottom - 3));
                    }
                }
            }
        }

        var detail = nonAudio
            ? $"MIDI / CONTROL {clip.MediaId} | no independent audio"
            : clip.MediaId is null
            ? Path.GetExtension(clip.SourcePath)
            : replaced
                ? $"WEM {clip.MediaId} / {clip.ReplacementMediaId?.ToString() ?? "NEW"}"
                : $"WEM {clip.MediaId}";
        var playlist = clip.PlaylistIndex is null ? string.Empty : $"  item {clip.PlaylistIndex.Value + 1}";
        var fade = (clip.HasFadeIn, clip.HasFadeOut) switch
        {
            (true, true) => "  fade in/out",
            (true, false) => "  fade in",
            (false, true) => "  fade out",
            _ => string.Empty
        };
        var fitText = fit.RepeatedMs > 1
            ? $"  repeat {fit.RepeatedMs / 1000:0.###}s"
            : fit.UnusedTailMs > 1
                ? $"  tail {fit.UnusedTailMs / 1000:0.###}s"
                : string.Empty;

        if (rect.Width >= 116)
        {
            DrawText(context, clip.Name, rect.Left + 12, rect.Top + 9, 12, nameColor, rect.Width - 24);
            DrawText(context, $"{detail}{playlist}{fade}{fitText}  {clip.DurationMs / 1000:0.###}s",
                rect.Left + 12, rect.Top + 32, 10, "#C8D9E5", rect.Width - 24);
        }
        else if (rect.Width >= 40)
        {
            DrawText(context, clip.Name, rect.Left + 9, rect.Top + 23, 10, nameColor, rect.Width - 18);
        }
    }

    private void DrawFades(DrawingContext context, MusicTimelineClip clip, Rect rect)
    {
        var pen = new Pen(Brush("#F7D58A"), 1.4);
        if (clip.FadeInMs > 0)
        {
            var end = Math.Min(rect.Right, XAt(clip.StartMs + clip.FadeInMs));
            context.DrawLine(pen, new Point(rect.Left, rect.Bottom - 7), new Point(end, rect.Top + 7));
        }

        if (clip.FadeOutMs > 0)
        {
            var start = Math.Max(rect.Left, XAt(clip.StartMs + clip.DurationMs - clip.FadeOutMs));
            context.DrawLine(pen, new Point(start, rect.Top + 7), new Point(rect.Right, rect.Bottom - 7));
        }
    }

    private void DrawWaveform(DrawingContext context, MusicTimelineClip clip, Rect rect, bool replaced)
    {
        if (!waveforms.TryGetValue(AudioKey(clip), out var waveform)
            || waveform.Points == 0 || waveform.DurationMs <= 0 || rect.Width <= 24)
        {
            if (rect.Width > 24)
            {
                context.DrawLine(new Pen(Brush("#728996")),
                    new Point(rect.Left + 10, rect.Center.Y),
                    new Point(rect.Right - 10, rect.Center.Y));
            }

            return;
        }

        var left = Math.Max(headerWidth, rect.Left + HandleWidth + 2);
        var right = Math.Min(Bounds.Width, rect.Right - HandleWidth - 2);
        var amplitude = Math.Max(4, rect.Height * 0.28);
        var pen = new Pen(Brush(replaced ? "#C3A16B" : "#849EAE"), 0.8);

        for (var x = left; x <= right; x++)
        {
            var relativeMs = (x - rect.Left) * 1_000 / PixelsPerSecond;
            var sourceMs = clip.SourceOffsetMs + relativeMs;

            if (clip.RepeatsSource)
            {
                sourceMs %= waveform.DurationMs;
            }

            if (sourceMs < 0 || sourceMs > waveform.DurationMs)
            {
                continue;
            }

            var sample = Math.Clamp(
                sourceMs / waveform.DurationMs * (waveform.Points - 1),
                0,
                waveform.Points - 1);
            var index = (int)sample;
            var next = Math.Min(waveform.Points - 1, index + 1);
            var fraction = (float)(sample - index);
            var maximum = waveform.Maximums[index] + (waveform.Maximums[next] - waveform.Maximums[index]) * fraction;
            var minimum = waveform.Minimums[index] + (waveform.Minimums[next] - waveform.Minimums[index]) * fraction;
            var top = rect.Center.Y - maximum * amplitude;
            var bottom = rect.Center.Y - minimum * amplitude;

            context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
        }
    }

    private void DrawPlayhead(DrawingContext context)
    {
        var x = XAt(PlayheadMs);
        if (x < headerWidth || x > Bounds.Width)
        {
            return;
        }

        context.DrawLine(new Pen(Brush("#F45B69"), 1.5), new Point(x, 0), new Point(x, Bounds.Height));
        context.FillRectangle(Brush("#F45B69"), new Rect(x - 4, 0, 8, 7));
    }

    private void DrawMarkersAndBoundary(DrawingContext context)
    {
        markerLabelHits.Clear();
        var segments = SegmentLayouts();
        if (segments.All(segment => segment.SegmentId is null) && document.TimelineLengthMs is { } length)
        {
            var x = XAt(length);
            if (x < Bounds.Width)
            {
                context.FillRectangle(Brush("#301C1217"), new Rect(Math.Max(headerWidth, x), RulerHeight,
                    Math.Max(0, Bounds.Width - Math.Max(headerWidth, x)), Bounds.Height - RulerHeight));
            }

            if (x >= headerWidth && x <= Bounds.Width)
            {
                context.DrawLine(new Pen(Brush("#F08B72"), 2), new Point(x, 0), new Point(x, Bounds.Height));
                DrawText(context, "END", x - 28, 7, 10, "#F3A28E");
            }
        }

        if (!ShowMarkers || standaloneAudioMode)
        {
            return;
        }

        var laneEnds = new Dictionary<uint, double[]>();
        foreach (var marker in document.Markers.OrderBy(item => item.PositionMs))
        {
            var positionMs = markerDrag?.Marker == marker && previewMarkerPositionMs is { } preview
                ? preview
                : marker.PositionMs;
            var x = XAt(positionMs);

            if (x < headerWidth || x > Bounds.Width)
            {
                continue;
            }

            var segment = segments.FirstOrDefault(item => item.SegmentId == marker.SegmentObjectId);
            if (marker.SegmentObjectId is not null && segment is null)
            {
                continue;
            }

            var top = segment?.TrackTop ?? RulerHeight;
            var bottom = segment?.Bottom ?? Bounds.Height;

            var markerColor = NameColorPalette.Hex(marker.Name.StartsWith("Entry", StringComparison.OrdinalIgnoreCase)
                ? "cue:Entry"
                : marker.Name.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)
                    ? "cue:Exit"
                    : $"cue:{marker.Name}");
            context.DrawLine(new Pen(Brush(markerColor), 1.4), new Point(x, top), new Point(x, bottom));

            var width = Math.Max(30, marker.Name.Length * 6.5 + 10);
            var left = Math.Clamp(x + 3, headerWidth + 2, Math.Max(headerWidth + 2, Bounds.Width - width - 2));
            var laneKey = marker.SegmentObjectId ?? 0;
            var lanes = laneEnds.GetValueOrDefault(laneKey);

            if (lanes is null)
            {
                lanes = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
                laneEnds[laneKey] = lanes;
            }

            var lane = Array.FindIndex(lanes, end => left > end + 3);
            if (lane < 0)
            {
                lane = Array.IndexOf(lanes, lanes.Min());
                left = Math.Max(left, lanes[lane] + 3);
            }

            if (left + width <= Bounds.Width - 2)
            {
                var labelTop = top + 3 + lane * 15;
                var labelBounds = new Rect(left, labelTop, width, 14);

                context.FillRectangle(Brush("#20242B"), labelBounds, 2);
                context.DrawRectangle(new Pen(Brush(markerColor), 1), labelBounds, 2);
                DrawText(context, marker.Name, left + 4, labelTop, 10, markerColor, width - 8);
                markerLabelHits.Add(new MarkerLabelHit(marker, labelBounds));
                lanes[lane] = left + width;
            }
        }
    }

    private void DrawSelection(DrawingContext context)
    {
        if (SelectionStartMs is not { } start || SelectionEndMs is not { } end || end - start <= 0.5)
        {
            return;
        }

        var left = Math.Max(headerWidth, XAt(start));
        var right = Math.Min(Bounds.Width, XAt(end));

        if (right > left)
        {
            context.FillRectangle(Brush("#454B5665"),
                new Rect(left, RulerHeight, right - left, Bounds.Height - RulerHeight));
            context.DrawLine(new Pen(Brush("#AEB9C5")), new Point(left, RulerHeight), new Point(left, Bounds.Height));
            context.DrawLine(new Pen(Brush("#AEB9C5")), new Point(right, RulerHeight), new Point(right, Bounds.Height));
        }
    }

    private void DrawTrackControls(DrawingContext context, MusicTimelineTrack track, double top)
    {
        if (!standaloneAudioMode)
        {
            DrawControlButton(context, MuteBounds(top), "M", track.IsMuted, "#C98465");
            DrawControlButton(context, SoloBounds(top), "S", track.IsSolo, "#D5B65D");
            DrawText(context, "Volume", 119, top + 63, 10, "#AAB4BE");
        }
        else
        {
            DrawText(context, "Volume", 12, top + 63, 10, "#AAB4BE");
        }

        var gain = GainBounds(top);
        context.DrawLine(new Pen(Brush("#596572"), 2),
            new Point(gain.Left, gain.Center.Y), new Point(gain.Right, gain.Center.Y));
        var x = gain.Left + Math.Clamp(track.Gain / 2, 0, 1) * gain.Width;
        context.FillRectangle(Brush("#D3DAE1"), new Rect(x - 5, gain.Center.Y - 9, 10, 18), 3);
        DrawText(
            context,
            GainText(track.Gain),
            gain.Left,
            top + 47,
            9,
            "#8996A4",
            gain.Width,
            TextAlignment.Center);
    }

    private static void DrawControlButton(
        DrawingContext context,
        Rect bounds,
        string text,
        bool active,
        string activeColor)
    {
        context.FillRectangle(Brush(active ? activeColor : "#222932"), bounds, 3);
        context.DrawRectangle(new Pen(Brush(active ? activeColor : "#596572")), bounds, 3);
        DrawText(context, text, bounds.Left + 15, bounds.Top + 4, 12, active ? "#11151A" : "#E0E5EA");
    }

    private static Rect MuteBounds(double top) => new(12, top + 61, 42, 27);
    private static Rect SoloBounds(double top) => new(62, top + 61, 42, 27);
    private Rect GainBounds(double top) => standaloneAudioMode
        ? new Rect(80, top + 61, Math.Max(48, headerWidth - 99), 27)
        : new Rect(171, top + 61, Math.Max(48, headerWidth - 190), 27);
    private Rect BpmEditorBounds(SegmentLayout segment) => new(headerWidth + 48, segment.Top + 9, 82, 28);
    private MusicTimelineMarker? MarkerAt(Point point)
    {
        if (point.X < headerWidth)
        {
            return null;
        }

        var label = markerLabelHits.LastOrDefault(item => item.Bounds.Contains(point));
        if (label is not null)
        {
            return label.Marker;
        }

        foreach (var marker in document.Markers)
        {
            var segment = SegmentLayouts().FirstOrDefault(item => item.SegmentId == marker.SegmentObjectId);
            if (segment is not null && point.Y >= segment.TrackTop && point.Y <= segment.Bottom
                && Math.Abs(point.X - XAt(marker.PositionMs)) <= 6)
            {
                return marker;
            }
        }

        return null;
    }

    private void NotifyBpmEditorPlacement()
    {
        if (standaloneAudioMode)
        {
            SegmentBpmEditorPlacementChanged?.Invoke(null, null);
            return;
        }

        var segment = SegmentLayouts().FirstOrDefault(item =>
            item.SegmentId == AuditionSegmentId
            && item.SegmentId is not null
            && item.Top < Bounds.Height
            && item.TrackTop > RulerHeight);
        SegmentBpmEditorPlacementChanged?.Invoke(
            segment?.SegmentId,
            segment is null ? null : BpmEditorBounds(segment));
    }

    private void SetTrackGainFromPointer(MusicTimelineTrack track, double x, double top)
    {
        var bounds = GainBounds(top);
        var gain = Math.Clamp((x - bounds.Left) / bounds.Width * 2, 0, 2);

        document.SetTrackGain(track.Id, gain);

        TrackMixChanged?.Invoke();
        StatusChanged?.Invoke($"{track.Name}: preview gain {GainText(gain)}");
    }

    private static string GainText(double gain)
    {
        if (gain <= 0.0001)
        {
            return "-inf dB";
        }

        var db = 20 * Math.Log10(gain);
        return $"{(db >= 0 ? "+" : string.Empty)}{db:0.0} dB";
    }

    private IReadOnlyList<SegmentGroup> SegmentGroups() => VisibleTracks
        .GroupBy(track => track.SegmentObjectId)
        .Select(group =>
        {
            var tracks = group.ToArray();
            var name = group.Key is null
                ? "Manual arrangement"
                : tracks.Select(track => track.Name)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?? $"Segment {group.Key.Value}";

            return new SegmentGroup(group.Key, name, tracks);
        })
        .ToArray();

    private IReadOnlyList<SegmentLayout> SegmentLayouts()
    {
        var layouts = new List<SegmentLayout>();
        var top = RulerHeight - verticalOffset;

        foreach (var group in SegmentGroups())
        {
            var trackTop = top + (standaloneAudioMode ? 0 : SegmentHeaderHeight);
            var rows = group.Tracks.Select((track, index) =>
                new TrackLayout(track, trackTop + index * TrackHeight, group.SegmentId)).ToArray();
            var bottom = trackTop + rows.Length * TrackHeight;
            var length = group.Tracks.Select(track => track.LengthMs)
                .OfType<double>()
                .DefaultIfEmpty()
                .Max();

            layouts.Add(new SegmentLayout(
                group.SegmentId,
                group.Name,
                group.Tracks,
                rows,
                top,
                trackTop,
                bottom,
                length > 0 ? length : null));
            top = bottom;
        }

        return layouts;
    }

    private IReadOnlyList<TrackLayout> TrackLayouts() => SegmentLayouts()
        .SelectMany(segment => segment.TrackRows)
        .ToArray();

    private TrackLayout? TrackLayoutAt(double y, bool clamp = false)
    {
        var layouts = TrackLayouts();
        var exact = layouts.FirstOrDefault(item => y >= item.Top && y < item.Top + TrackHeight);

        if (exact is not null || !clamp || layouts.Count == 0)
        {
            return exact;
        }

        return layouts.MinBy(item => Math.Abs(y - (item.Top + TrackHeight / 2)));
    }

    private static string SegmentTitle(SegmentLayout segment) => segment.SegmentId is { } id
        ? $"{segment.Name} (Segment {id})"
        : segment.Name;

    private static string TrackDisplayName(MusicTimelineTrack track) => !string.IsNullOrWhiteSpace(track.Name)
        ? track.Name
        : track.ObjectId?.ToString(CultureInfo.InvariantCulture) ?? "Manual track";

    private bool SelectAuditionSegment(MusicTimelineTrack track)
        => SelectAuditionSegment(track.SegmentObjectId);

    private bool SelectAuditionSegment(uint? segmentId)
    {
        if (segmentId is null || segmentId == auditionSegmentId)
        {
            return false;
        }

        auditionSegmentId = segmentId;
        NotifyBpmEditorPlacement();

        return true;
    }

    private Hit? HitTest(Point point)
    {
        foreach (var row in TrackLayouts())
        {
            var track = row.Track;
            foreach (var clip in track.Clips.AsEnumerable().Reverse())
            {
                var bounds = new Rect(
                    XAt(clip.StartMs),
                    row.Top + 8,
                    clip.DurationMs * PixelsPerSecond / 1000,
                    TrackHeight - 16);
                if (bounds.Contains(point))
                {
                    return new Hit(track, clip, bounds);
                }
            }
        }

        return null;
    }

    private void DocumentChanged()
    {
        FitHeaderToTrackNames();
        SyncTrackNameLabels();

        var tracks = VisibleTracks;
        if (SelectedTrackId is null || tracks.All(track => track.Id != SelectedTrackId))
        {
            SelectedTrackId = tracks.FirstOrDefault()?.Id;
        }

        if (document.TimelineLengthMs is { } length && PlayheadMs > length)
        {
            PlayheadMs = 0;
        }

        verticalOffset = Math.Min(verticalOffset, MaxVerticalOffset());

        EnsureSelectionExists();
        UpdateTrackNameLabels();
        InvalidateArrange();
        InvalidateVisual();
        NotifyBpmEditorPlacement();
    }

    private void EnsureSelectionExists()
    {
        if (SelectedClipId is not { } id)
        {
            return;
        }

        try
        {
            document.FindClip(id);
        }
        catch (KeyNotFoundException)
        {
            SelectedClipId = null;
            SelectedMediaId = null;

            SelectionChanged?.Invoke();
        }
    }

    private double XAt(double timeMs) => headerWidth + (timeMs - horizontalOffsetMs) * PixelsPerSecond / 1000;
    private double TimeAt(double x) => Math.Max(0, horizontalOffsetMs + (x - headerWidth) * 1000 / PixelsPerSecond);
    private double MaxVerticalOffset() => Math.Max(0,
        RulerHeight + (standaloneAudioMode ? 0 : SegmentGroups().Count * SegmentHeaderHeight)
        + VisibleTracks.Count * TrackHeight - Bounds.Height);

    private double MaximumHeaderWidth() => Math.Max(MinimumHeaderWidth, Bounds.Width - 260);

    private void FitHeaderToTrackNames()
    {
        var tracks = VisibleTracks;
        if (userSizedHeader || tracks.Count == 0)
        {
            return;
        }

        var longest = SegmentGroups().Select(group => group.Name.Length)
            .Concat(tracks.Select(track => TrackDisplayName(track).Length))
            .DefaultIfEmpty()
            .Max();
        headerWidth = Math.Clamp(longest * 8.2 + 28, MinimumHeaderWidth, MaximumHeaderWidth());
    }

    private void SyncTrackNameLabels()
    {
        var current = document.Tracks.Select(track => track.Id).ToHashSet();
        foreach (var stale in trackNameLabels.Keys.Where(id => !current.Contains(id)).ToArray())
        {
            Children.Remove(trackNameLabels[stale]);
            trackNameLabels.Remove(stale);
        }

        foreach (var track in document.Tracks.Where(track => !trackNameLabels.ContainsKey(track.Id)))
        {
            var label = new SelectableTextBlock
            {
                Tag = track.Id,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.PointerPressed += (_, _) => SelectTrackLabel(track.Id);
            trackNameLabels.Add(track.Id, label);
            Children.Add(label);
        }

        UpdateTrackNameLabels();
        InvalidateMeasure();
        InvalidateArrange();
    }

    private void UpdateTrackNameLabels()
    {
        foreach (var track in document.Tracks)
        {
            if (!trackNameLabels.TryGetValue(track.Id, out var label))
            {
                continue;
            }

            label.Text = TrackDisplayName(track);
            label.Foreground = Brush(track.Id == SelectedTrackId
                ? "#89F0E9"
                : dirtyTrackIds.Contains(track.Id) ? "#FFC36E" : "#E6EAF0");
        }
    }

    private void SelectTrackLabel(Guid trackId)
    {
        var track = document.Tracks.FirstOrDefault(item => item.Id == trackId);
        if (track is null)
        {
            return;
        }

        SelectedTrackId = track.Id;
        SelectedClipId = null;
        SelectedMediaId = null;

        var auditionChanged = SelectAuditionSegment(track);
        UpdateTrackNameLabels();
        SelectionChanged?.Invoke();
        if (auditionChanged)
        {
            AuditionSegmentChanged?.Invoke();
        }

        InvalidateVisual();
        NotifyBpmEditorPlacement();
    }

    private static void DrawText(
        DrawingContext context,
        string value,
        double x,
        double y,
        double size,
        string color,
        double maxWidth = double.PositiveInfinity,
        TextAlignment alignment = TextAlignment.Left)
    {
        if (maxWidth <= 1)
        {
            return;
        }

        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, Brush(color))
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment
        };
        context.DrawText(text, new Point(x, y));
    }

    private sealed record MarkerLabelHit(MusicTimelineMarker Marker, Rect Bounds);

    private sealed class TimelineRenderer(TimelineControl owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            owner.RenderTimeline(context);
        }
    }

    private static SolidColorBrush Brush(string value) => new(Color.Parse(value));
    private static string FormatTime(double value) => TimeSpan.FromMilliseconds(value).ToString("m\\:ss\\.fff", CultureInfo.InvariantCulture);

    private static string AudioKey(MusicTimelineClip clip) => clip.SourcePath is { } path
        ? $"file:{Path.GetFullPath(path)}"
        : $"media:{clip.MediaId?.ToString(CultureInfo.InvariantCulture) ?? "none"}";

    private enum DragMode { Move, TrimStart, TrimEnd }
    private sealed record DragState(Point StartPoint, MusicTimelineClip Clip, DragMode Mode);
    private sealed record MarkerDragState(MusicTimelineMarker Marker);
    private sealed record SelectionDragState(double AnchorMs, uint? SegmentId);
    private sealed record SegmentGroup(uint? SegmentId, string Name, IReadOnlyList<MusicTimelineTrack> Tracks);
    private sealed record SegmentLayout(
        uint? SegmentId,
        string Name,
        IReadOnlyList<MusicTimelineTrack> Tracks,
        IReadOnlyList<TrackLayout> TrackRows,
        double Top,
        double TrackTop,
        double Bottom,
        double? LengthMs);
    private sealed record TrackLayout(MusicTimelineTrack Track, double Top, uint? SegmentId);
    private readonly record struct Hit(MusicTimelineTrack Track, MusicTimelineClip Clip, Rect Bounds);
}
