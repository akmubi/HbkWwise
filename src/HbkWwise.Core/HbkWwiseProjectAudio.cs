using System.Text.Json;

namespace HbkWwise.Core;

public static class HbkWwiseProjectAudio
{
    public static async Task<HbkWwiseProjectAudioRepair> RepairAsync(
        HbkWwiseProject project,
        string projectPath,
        string? vgmstreamPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var original = JsonSerializer.Serialize(project);
        project = await Task.Run(() => HbkWwiseProjectAssets.Localize(
            ResolveMovedSources(project, projectPath),
            projectPath,
            projectPath), cancellationToken);
        var root = HbkWwiseProjectAssets.AudioRoot(projectPath);
        var convertedDirectory = Path.Combine(root, "Converted");
        var imported = project.ImportedAudio.ToArray();
        var regeneratedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rebuilt = 0;

        for (var index = 0; index < imported.Length; index++)
        {
            var audio = imported[index];
            if (!RequiresWorkingWav(audio.Path)
                || !File.Exists(audio.Path)
                || !string.IsNullOrWhiteSpace(audio.WorkingPath) && File.Exists(audio.WorkingPath))
            {
                continue;
            }

            var wav = await PrepareWavAsync(
                audio.Path,
                convertedDirectory,
                vgmstreamPath,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(audio.WorkingPath))
            {
                regeneratedPaths[Path.GetFullPath(audio.WorkingPath)] = wav;
            }
            imported[index] = audio with { WorkingPath = wav };
            rebuilt++;
        }

        project = project with { ImportedAudio = imported };
        if (regeneratedPaths.Count > 0)
        {
            project = HbkWwiseProjectAssets.Rewrite(project, path =>
                regeneratedPaths.TryGetValue(Path.GetFullPath(path), out var regenerated)
                    ? regenerated
                    : path);
        }
        var recipes = (project.GeneratedAudio ?? [])
            .GroupBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var reference in References(project)
            .Where(item => !File.Exists(item.Path)
                && IsBelow(item.Path, Path.Combine(root, "Generated")))
            .GroupBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Path = group.Key,
                Names = group.Select(item => item.Name).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                PhysicalDurationMs = group.Select(item => item.PhysicalDurationMs)
                    .FirstOrDefault(value => value > 0)
            }))
        {
            if (recipes.ContainsKey(reference.Path))
            {
                continue;
            }

            var source = imported.FirstOrDefault(audio => File.Exists(audio.Path)
                && !IsBelow(audio.Path, Path.Combine(root, "Generated"))
                && !IsBelow(audio.Path, Path.Combine(root, "Converted"))
                && reference.Names.Any(name => AudioNamesEqual(name, audio)));
            if (source is null)
            {
                continue;
            }

            var duration = source.Format.DurationSeconds * 1000;
            var leadingSilence = reference.PhysicalDurationMs - duration;
            if (!double.IsFinite(leadingSilence) || leadingSilence is < -1 or > 30_000)
            {
                continue;
            }

            recipes[reference.Path] = new HbkProjectGeneratedAudio(
                reference.Path,
                source.Path,
                Math.Max(0, leadingSilence),
                0,
                duration);
        }

        var pending = recipes.Values.Where(item => !File.Exists(item.Path)).ToList();
        var madeProgress = true;
        while (pending.Count > 0 && madeProgress)
        {
            madeProgress = false;
            foreach (var recipe in pending.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(recipe.SourcePath))
                {
                    continue;
                }

                var source = await PrepareWavAsync(
                    recipe.SourcePath,
                    convertedDirectory,
                    vgmstreamPath,
                    cancellationToken);
                Directory.CreateDirectory(Path.GetDirectoryName(recipe.Path)!);
                await Task.Run(() => TimelineAudioRenderer.Render(
                    [new TimelineAudioPlacement(
                        source,
                        recipe.LeadingSilenceMs,
                        recipe.SourceOffsetMs,
                        recipe.DurationMs,
                        recipe.RepeatsSource,
                        recipe.FadeInMs,
                        recipe.FadeOutMs)],
                    recipe.Path,
                    cancellationToken), cancellationToken);
                pending.Remove(recipe);
                rebuilt++;
                madeProgress = true;
            }
        }

        project = project with { GeneratedAudio = recipes.Values.ToArray() };
        var deduplicated = DeduplicateImports(project);
        project = deduplicated.Project;
        return new HbkWwiseProjectAudioRepair(
            project,
            rebuilt,
            deduplicated.RemovedMediaIds,
            !string.Equals(original, JsonSerializer.Serialize(project), StringComparison.Ordinal));
    }

    private static DeduplicatedProject DeduplicateImports(HbkWwiseProject project)
    {
        var imports = project.Imports.Concat(
            (project.Timelines ?? []).SelectMany(timeline => timeline.Imports));
        var remapped = imports.GroupBy(item =>
                $"{Path.GetFullPath(item.Path)}\0{Math.Round(item.PhysicalDurationMs, 3):F3}",
                StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Select(item => item.NewMediaId).Distinct().Skip(1)
                .Select(id => (Duplicate: id, Canonical: group.First().NewMediaId)))
            .GroupBy(item => item.Duplicate)
            .ToDictionary(group => group.Key, group => group.First().Canonical);
        if (remapped.Count == 0)
        {
            return new DeduplicatedProject(project, 0);
        }

        HbkProjectTrack[] Tracks(IEnumerable<HbkProjectTrack> tracks) => tracks.Select(track => track with
        {
            Clips = track.Clips.Select(clip => clip.ReplacementMediaId is { } id
                    && remapped.TryGetValue(id, out var canonical)
                ? clip with { ReplacementMediaId = canonical }
                : clip).ToArray()
        }).ToArray();

        HbkProjectImport[] Imports(IEnumerable<HbkProjectImport> source) => source
            .Select(item => remapped.TryGetValue(item.NewMediaId, out var canonical)
                ? item with { NewMediaId = canonical }
                : item)
            .GroupBy(item => item.NewMediaId)
            .Select(group => group.First())
            .ToArray();

        return new DeduplicatedProject(project with
        {
            Tracks = Tracks(project.Tracks),
            Imports = Imports(project.Imports),
            Timelines = project.Timelines?.Select(timeline => timeline with
            {
                Tracks = Tracks(timeline.Tracks),
                Imports = Imports(timeline.Imports)
            }).ToArray()
        }, remapped.Count);
    }

    private static async Task<string> PrepareWavAsync(
        string sourcePath,
        string directory,
        string? vgmstreamPath,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        if (Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var stamp = File.GetLastWriteTimeUtc(source).Ticks;
        var id = WwiseHash.Fnv1($"{source}|{stamp}");
        var name = SafeFileName(Path.GetFileNameWithoutExtension(source));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"{name}-{id}.wav");
        if (!File.Exists(output))
        {
            await VgmstreamClient.DecodeAsync(source, output, vgmstreamPath, cancellationToken);
        }

        return output;
    }

    private static IEnumerable<AudioReference> References(HbkWwiseProject project)
    {
        foreach (var item in References(project.Tracks, project.Replacements, project.Imports))
        {
            yield return item;
        }

        foreach (var timeline in project.Timelines ?? [])
        {
            foreach (var item in References(timeline.Tracks, timeline.Replacements, timeline.Imports))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<AudioReference> References(
        IEnumerable<HbkProjectTrack> tracks,
        IEnumerable<HbkProjectReplacement> replacements,
        IEnumerable<HbkProjectImport> imports)
    {
        foreach (var clip in tracks.SelectMany(track => track.Clips)
            .Where(clip => !string.IsNullOrWhiteSpace(clip.SourcePath)))
        {
            yield return new AudioReference(
                clip.SourcePath!, clip.Name, clip.PhysicalDurationMs ?? 0);
        }

        foreach (var replacement in replacements)
        {
            yield return new AudioReference(
                replacement.Path, null, replacement.PhysicalDurationMs);
        }

        foreach (var import in imports)
        {
            yield return new AudioReference(import.Path, null, import.PhysicalDurationMs);
        }
    }

    private static bool RequiresWorkingWav(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    private static HbkWwiseProject ResolveMovedSources(HbkWwiseProject project, string projectPath)
    {
        var missing = project.ImportedAudio.Where(audio => !File.Exists(audio.Path)
                && IsSupportedAudio(audio.Path)
                && !IsAppCachePath(audio.Path))
            .ToArray();
        if (missing.Length == 0)
        {
            return project;
        }

        var roots = new[]
        {
            Path.GetDirectoryName(Path.GetFullPath(projectPath)),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        }.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
        var wantedExtensions = missing.Select(audio => Path.GetExtension(audio.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var candidates = roots.SelectMany(root => Directory.EnumerateFiles(root, "*", options))
            .Where(path => wantedExtensions.Contains(Path.GetExtension(path)))
            .GroupBy(path => (Extension: Path.GetExtension(path).ToUpperInvariant(), Title: AudioTitle(path)))
            .ToDictionary(group => group.Key, group => group.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var audio in missing)
        {
            var key = (Path.GetExtension(audio.Path).ToUpperInvariant(), AudioTitle(audio.Name));
            if (candidates.TryGetValue(key, out var matches) && matches.Length == 1)
            {
                paths[Path.GetFullPath(audio.Path)] = Path.GetFullPath(matches[0]);
            }
        }

        return paths.Count == 0
            ? project
            : HbkWwiseProjectAssets.Rewrite(project, path =>
                paths.TryGetValue(Path.GetFullPath(path), out var moved) ? moved : path);
    }

    private static bool IsSupportedAudio(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAppCachePath(string path)
    {
        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HbkWwise");
        return IsBelow(path, cache);
    }

    private static string AudioTitle(string value)
    {
        var title = (IsSupportedAudio(value) ? Path.GetFileNameWithoutExtension(value) : value).TrimStart();
        var offset = 0;
        while (offset < title.Length && char.IsDigit(title[offset]))
        {
            offset++;
        }

        while (offset < title.Length && (char.IsWhiteSpace(title[offset]) || char.IsPunctuation(title[offset])))
        {
            offset++;
        }

        return new string(title[offset..].Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant).ToArray());
    }

    private static bool AudioNamesEqual(string name, HbkProjectAudio audio) =>
        name.Equals(audio.Name, StringComparison.OrdinalIgnoreCase)
        || name.Equals(Path.GetFileNameWithoutExtension(audio.Path), StringComparison.OrdinalIgnoreCase);

    private static bool IsBelow(string path, string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "audio" : cleaned;
    }

    private sealed record AudioReference(string Path, string? Name, double PhysicalDurationMs);

    private sealed record DeduplicatedProject(HbkWwiseProject Project, int RemovedMediaIds);
}

public sealed record HbkWwiseProjectAudioRepair(
    HbkWwiseProject Project,
    int RebuiltFiles,
    int DeduplicatedMedia,
    bool NeedsSave);
