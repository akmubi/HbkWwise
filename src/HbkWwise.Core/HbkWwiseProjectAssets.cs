using System.Security.Cryptography;

namespace HbkWwise.Core;

public static class HbkWwiseProjectAssets
{
    public static HbkWwiseProject Localize(
        HbkWwiseProject project,
        string projectPath,
        string? previousProjectPath = null) =>
        LocalizeWithMap(project, projectPath, previousProjectPath).Project;

    public static HbkWwiseProjectAssetRelocation LocalizeWithMap(
        HbkWwiseProject project,
        string projectPath,
        string? previousProjectPath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var output = Path.GetFullPath(projectPath);
        var root = Path.Combine(
            Path.GetDirectoryName(output)!,
            $"{Path.GetFileNameWithoutExtension(output)}_audio");
        var oldRoot = string.IsNullOrWhiteSpace(previousProjectPath)
            ? null
            : AudioRoot(previousProjectPath);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var audio in project.ImportedAudio)
        {
            Add(audio.WorkingPath, "Converted");
            if (File.Exists(audio.Path) || IsOwned(audio.Path, oldRoot))
            {
                Add(audio.Path, IsOwned(audio.Path, oldRoot) ? Category(audio.Path) : "Sources");
            }
        }

        foreach (var generated in project.GeneratedAudio ?? [])
        {
            Add(generated.Path, "Generated");
            if (File.Exists(generated.SourcePath) || IsOwned(generated.SourcePath, oldRoot))
            {
                Add(generated.SourcePath, "Sources");
            }
        }

        foreach (var path in ReferencedPaths(project).Where(path => IsOwned(path, oldRoot)))
        {
            Add(path, Category(path));
        }

        return new HbkWwiseProjectAssetRelocation(
            Rewrite(project, path => map.TryGetValue(Full(path), out var localized) ? localized : path),
            map);

        void Add(string? path, string category)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var source = Full(path);
            if (map.ContainsKey(source) || IsBelow(source, root))
            {
                return;
            }

            var directory = Path.Combine(root, category);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, Path.GetFileName(source));
            if (File.Exists(destination)
                && File.Exists(source)
                && !PathsEqual(source, destination)
                && !FilesEqual(source, destination))
            {
                destination = Path.Combine(
                    directory,
                    $"{Path.GetFileNameWithoutExtension(source)}-{WwiseHash.Fnv1(source)}{Path.GetExtension(source)}");
            }

            if (File.Exists(source) && !PathsEqual(source, destination))
            {
                File.Copy(source, destination, true);
            }

            map[source] = destination;
        }
    }

    public static HbkWwiseProject Rewrite(HbkWwiseProject project, Func<string, string> rewrite)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(rewrite);
        return project with
        {
            Tracks = RewriteTracks(project.Tracks, rewrite),
            ImportedAudio = project.ImportedAudio.Select(audio => audio with
            {
                Path = rewrite(audio.Path),
                WorkingPath = RewriteOptional(audio.WorkingPath, rewrite)
            }).ToArray(),
            Replacements = RewriteReplacements(project.Replacements, rewrite),
            Imports = RewriteImports(project.Imports, rewrite),
            Timelines = project.Timelines?.Select(timeline => timeline with
            {
                Tracks = RewriteTracks(timeline.Tracks, rewrite),
                Replacements = RewriteReplacements(timeline.Replacements, rewrite),
                Imports = RewriteImports(timeline.Imports, rewrite)
            }).ToArray(),
            GeneratedAudio = project.GeneratedAudio?.Select(generated => generated with
            {
                Path = rewrite(generated.Path),
                SourcePath = rewrite(generated.SourcePath)
            }).ToArray()
        };
    }

    public static string AudioRoot(string projectPath)
    {
        var output = Path.GetFullPath(projectPath);
        return Path.Combine(
            Path.GetDirectoryName(output)!,
            $"{Path.GetFileNameWithoutExtension(output)}_audio");
    }

    private static IEnumerable<string> ReferencedPaths(HbkWwiseProject project)
    {
        foreach (var path in Paths(project.Tracks, project.Replacements, project.Imports))
        {
            yield return path;
        }

        foreach (var timeline in project.Timelines ?? [])
        {
            foreach (var path in Paths(timeline.Tracks, timeline.Replacements, timeline.Imports))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> Paths(
        IEnumerable<HbkProjectTrack> tracks,
        IEnumerable<HbkProjectReplacement> replacements,
        IEnumerable<HbkProjectImport> imports) => tracks
            .SelectMany(track => track.Clips)
            .Select(clip => clip.SourcePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Concat(replacements.Select(item => item.Path))
            .Concat(imports.Select(item => item.Path));

    private static HbkProjectTrack[] RewriteTracks(
        IEnumerable<HbkProjectTrack> tracks,
        Func<string, string> rewrite) => tracks.Select(track => track with
        {
            Clips = track.Clips.Select(clip => clip with
            {
                SourcePath = RewriteOptional(clip.SourcePath, rewrite)
            }).ToArray()
        }).ToArray();

    private static HbkProjectReplacement[] RewriteReplacements(
        IEnumerable<HbkProjectReplacement> replacements,
        Func<string, string> rewrite) => replacements
            .Select(item => item with { Path = rewrite(item.Path) })
            .ToArray();

    private static HbkProjectImport[] RewriteImports(
        IEnumerable<HbkProjectImport> imports,
        Func<string, string> rewrite) => imports
            .Select(item => item with { Path = rewrite(item.Path) })
            .ToArray();

    private static string? RewriteOptional(string? path, Func<string, string> rewrite) =>
        string.IsNullOrWhiteSpace(path) ? path : rewrite(path);

    private static bool IsOwned(string path, string? oldRoot)
    {
        var full = Full(path);
        if (oldRoot is not null && IsBelow(full, oldRoot))
        {
            return true;
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HbkWwise");
        return IsBelow(full, appData);
    }

    private static string Category(string path)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(Full(path)));
        return folder?.ToUpperInvariant() switch
        {
            "CONVERTED" => "Converted",
            "SOURCES" => "Sources",
            _ => "Generated"
        };
    }

    private static bool IsBelow(string path, string root)
    {
        var prefix = Full(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Full(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Full(left), Full(right), StringComparison.OrdinalIgnoreCase);

    private static bool FilesEqual(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length)
        {
            return false;
        }

        using var leftStream = File.OpenRead(left);
        using var rightStream = File.OpenRead(right);
        return SHA256.HashData(leftStream).SequenceEqual(SHA256.HashData(rightStream));
    }

    private static string Full(string path) => Path.GetFullPath(path);
}

public sealed record HbkWwiseProjectAssetRelocation(
    HbkWwiseProject Project,
    IReadOnlyDictionary<string, string> PathMap);
