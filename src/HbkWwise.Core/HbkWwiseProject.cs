using System.Text.Json;
using System.Text.Json.Serialization;

namespace HbkWwise.Core;

public sealed record HbkWwiseProject(
    int Version,
    string? IndexPath,
    HbkProjectComposition? Composition,
    double Bpm,
    int BeatsPerBar,
    int SubdivisionsPerBeat,
    bool SnapEnabled,
    double? TimelineLengthMs,
    HbkProjectTrack[] Tracks,
    MusicTimelineMarker[] Markers,
    HbkProjectAudio[] ImportedAudio,
    HbkProjectReplacement[] Replacements,
    HbkProjectImport[] Imports,
    HbkProjectSegmentTempo[]? SegmentTempos = null,
    HbkProjectTimeline[]? Timelines = null,
    Guid? ActiveTimelineId = null,
    string[]? PinnedClipKeys = null,
    uint[]? MetronomeSegments = null)
{
    public const int CurrentVersion = 1;
}

public sealed record HbkProjectComposition(
    uint EventId,
    uint SegmentId,
    uint ScopeId,
    double AuthoredBpm);

public sealed record HbkProjectTrack(
    Guid Id,
    string Name,
    uint? ObjectId,
    uint? SegmentObjectId,
    double? LengthMs,
    HbkProjectClip[] Clips,
    bool IsMuted = false,
    bool IsSolo = false,
    double Gain = 1);

public sealed record HbkProjectClip(
    Guid Id,
    string Name,
    uint? MediaId,
    string? SourcePath,
    double StartMs,
    double SourceOffsetMs,
    double DurationMs,
    uint? ReplacementMediaId,
    double? PhysicalDurationMs,
    bool RepeatsSource,
    HbkProjectClipAnchor? Anchor,
    double? FadeInMs = null,
    double? FadeOutMs = null);

public sealed record HbkProjectClipAnchor(
    uint TrackObjectId,
    uint? SegmentObjectId,
    int PlaylistIndex,
    uint MediaId);

public sealed record HbkProjectAudio(
    Guid Id,
    string Name,
    string Path,
    MediaFormat Format,
    string? WorkingPath = null);

public sealed record HbkProjectReplacement(
    uint OriginalMediaId,
    uint NewMediaId,
    string Path,
    double PhysicalDurationMs);

public sealed record HbkProjectImport(
    uint TemplateMediaId,
    uint NewMediaId,
    string Path,
    double PhysicalDurationMs);

public sealed record HbkProjectSegmentTempo(uint SegmentId, double Bpm);

public sealed record HbkProjectTimeline(
    Guid Id,
    string Name,
    HbkProjectComposition? Composition,
    double Bpm,
    int BeatsPerBar,
    int SubdivisionsPerBeat,
    bool SnapEnabled,
    double? TimelineLengthMs,
    HbkProjectTrack[] Tracks,
    MusicTimelineMarker[] Markers,
    HbkProjectReplacement[] Replacements,
    HbkProjectImport[] Imports,
    HbkProjectSegmentTempo[] SegmentTempos,
    uint[]? MetronomeSegments = null,
    uint[]? VisibleSegmentIds = null,
    uint? OccurrenceMediaId = null,
    uint? InspectionEventId = null,
    uint? StandaloneMediaId = null,
    string? StandaloneMediaBank = null);

public static class HbkWwiseProjectStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task SaveAsync(
        HbkWwiseProject project,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);

        var output = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var temporary = $"{output}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, project, Options, cancellationToken);
            }

            File.Move(temporary, output, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static async Task<HbkWwiseProject> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(Path.GetFullPath(path));
        var project = await JsonSerializer.DeserializeAsync<HbkWwiseProject>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Project file is empty.");

        Validate(project);
        return project;
    }

    private static void Validate(HbkWwiseProject project)
    {
        if (project.Version != HbkWwiseProject.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported project version {project.Version}; expected {HbkWwiseProject.CurrentVersion}.");
        }

        if (project.ImportedAudio is null || project.Replacements is null || project.Imports is null)
        {
            throw new InvalidDataException("Project collections are missing.");
        }

        ValidateTimelineData(
            project.Bpm,
            project.BeatsPerBar,
            project.SubdivisionsPerBeat,
            project.TimelineLengthMs,
            project.Tracks,
            project.Markers,
            project.SegmentTempos,
            requireSegmentTempos: false,
            requireEditorBpmRange: false,
            errorMessage: "Project timeline data is invalid.");

        if (project.Timelines is null)
        {
            return;
        }

        if (project.Timelines.GroupBy(timeline => timeline.Id).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Project timeline-tab data is invalid.");
        }

        foreach (var timeline in project.Timelines)
        {
            if (timeline.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(timeline.Name)
                || timeline.Replacements is null
                || timeline.Imports is null)
            {
                throw new InvalidDataException("Project timeline-tab data is invalid.");
            }

            ValidateTimelineData(
                timeline.Bpm,
                timeline.BeatsPerBar,
                timeline.SubdivisionsPerBeat,
                timeline.TimelineLengthMs,
                timeline.Tracks,
                timeline.Markers,
                timeline.SegmentTempos,
                requireSegmentTempos: true,
                requireEditorBpmRange: true,
                errorMessage: "Project timeline-tab data is invalid.");
        }
    }

    private static void ValidateTimelineData(
        double bpm,
        int beatsPerBar,
        int subdivisionsPerBeat,
        double? timelineLengthMs,
        HbkProjectTrack[]? tracks,
        MusicTimelineMarker[]? markers,
        HbkProjectSegmentTempo[]? segmentTempos,
        bool requireSegmentTempos,
        bool requireEditorBpmRange,
        string errorMessage)
    {
        if (!double.IsFinite(bpm)
            || bpm <= 0
            || requireEditorBpmRange && bpm is < 20 or > 400
            || beatsPerBar <= 0
            || subdivisionsPerBeat <= 0
            || tracks is null
            || markers is null
            || requireSegmentTempos && segmentTempos is null)
        {
            throw new InvalidDataException(errorMessage);
        }

        if (timelineLengthMs is { } length && (!double.IsFinite(length) || length <= 0))
        {
            throw new InvalidDataException(errorMessage);
        }

        if (segmentTempos?.Any(item => item.SegmentId == 0
                || !double.IsFinite(item.Bpm)
                || item.Bpm is < 20 or > 400) == true)
        {
            throw new InvalidDataException(errorMessage);
        }

        if (tracks.Any(track => string.IsNullOrWhiteSpace(track.Name)
                || track.Clips is null
                || !double.IsFinite(track.Gain)
                || track.Gain is < 0 or > 2))
        {
            throw new InvalidDataException(errorMessage);
        }

        if (tracks.SelectMany(track => track.Clips).Any(clip =>
                !double.IsFinite(clip.StartMs)
                || clip.StartMs < 0
                || !double.IsFinite(clip.SourceOffsetMs)
                || clip.SourceOffsetMs < 0
                || !double.IsFinite(clip.DurationMs)
                || clip.DurationMs <= 0
                || clip.FadeInMs is { } fadeIn
                    && (!double.IsFinite(fadeIn) || fadeIn < 0 || fadeIn > clip.DurationMs)
                || clip.FadeOutMs is { } fadeOut
                    && (!double.IsFinite(fadeOut) || fadeOut < 0 || fadeOut > clip.DurationMs)))
        {
            throw new InvalidDataException(errorMessage);
        }
    }
}

public static class HbkWwiseProjectTimeline
{
    public static MusicTimelineTrack[] RestoreTracks(
        HbkWwiseProject project,
        BnkTimelineValidation? validation)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Tracks.Select(track => new MusicTimelineTrack(
            track.Id,
            track.Name,
            track.Clips.Select(clip => RestoreClip(clip, validation)).ToArray(),
            track.ObjectId,
            track.SegmentObjectId,
            track.LengthMs,
            track.IsMuted,
            track.IsSolo,
            track.Gain)).ToArray();
    }

    private static MusicTimelineClip RestoreClip(HbkProjectClip clip, BnkTimelineValidation? validation)
    {
        if (clip.Anchor is not { } anchor)
        {
            return new MusicTimelineClip(
                clip.Id,
                clip.MediaId,
                clip.Name,
                clip.SourcePath,
                clip.StartMs,
                clip.SourceOffsetMs,
                clip.DurationMs,
                ReplacementMediaId: clip.ReplacementMediaId,
                PhysicalDurationMs: clip.PhysicalDurationMs,
                HasFadeIn: clip.FadeInMs > 0,
                HasFadeOut: clip.FadeOutMs > 0,
                RepeatsSource: clip.RepeatsSource,
                FadeInMs: clip.FadeInMs ?? 0,
                FadeOutMs: clip.FadeOutMs ?? 0);
        }

        var source = validation?.Clips.FirstOrDefault(item =>
                item.TrackObjectId == anchor.TrackObjectId
                && item.SegmentObjectId == anchor.SegmentObjectId
                && item.PlaylistIndex == anchor.PlaylistIndex
                && item.MediaId == anchor.MediaId)
            ?? validation?.Clips.FirstOrDefault(item =>
                item.TrackObjectId == anchor.TrackObjectId
                && item.SegmentObjectId == anchor.SegmentObjectId
                && item.PlaylistIndex == anchor.PlaylistIndex)
            ?? throw new InvalidDataException(
                $"Authored playlist item {anchor.PlaylistIndex + 1} on track {anchor.TrackObjectId} no longer exists.");

        var fadeInMs = clip.FadeInMs ?? source.FadeInMs;
        var fadeOutMs = clip.FadeOutMs ?? source.FadeOutMs;

        return new MusicTimelineClip(
            clip.Id,
            source.MediaId,
            clip.Name,
            clip.SourcePath,
            clip.StartMs,
            clip.SourceOffsetMs,
            clip.DurationMs,
            source.SourceIdOffset,
            clip.ReplacementMediaId,
            source.FieldOffsets,
            clip.SourcePath is null ? source.SourceDurationMs : clip.PhysicalDurationMs,
            source.PlaylistIndex,
            fadeInMs > 0,
            fadeOutMs > 0,
            clip.RepeatsSource,
            fadeInMs,
            fadeOutMs);
    }
}
