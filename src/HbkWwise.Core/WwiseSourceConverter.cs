using System.Xml.Linq;

namespace HbkWwise.Core;

public sealed record WwiseSourceInput(uint MediaId, string SourcePath);

public static class WwiseSourceConverter
{
    private const string Conversion = "Vorbis Quality High";

    public static async Task<IReadOnlyDictionary<uint, string>> ConvertAsync(
        IReadOnlyCollection<WwiseSourceInput> sources,
        string workingDirectory,
        string? wwiseConsolePath = null,
        string? vgmstreamPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || sources.GroupBy(item => item.MediaId).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("Source media IDs must be non-empty and unique.", nameof(sources));
        }

        var resolved = sources.ToDictionary(
            item => item.MediaId,
            item => ExistingSource(item.SourcePath));
        var unsupported = resolved.Where(item =>
            !SupportedExtension(Path.GetExtension(item.Value))).ToArray();

        if (unsupported.Length > 0)
        {
            throw new InvalidDataException(
                $"Unsupported replacement source '{Path.GetExtension(unsupported[0].Value)}'. Use WAV, MP3, FLAC, OGG, or a ready WEM.");
        }

        var root = Path.GetFullPath(workingDirectory);
        var compressed = resolved.Where(item => IsCompressed(Path.GetExtension(item.Value))).ToArray();
        foreach (var item in compressed)
        {
            var decoded = Path.Combine(root, "Decoded", $"{item.Key}.wav");
            await VgmstreamClient.DecodeAsync(item.Value, decoded, vgmstreamPath, cancellationToken);
            resolved[item.Key] = decoded;
        }

        var wav = resolved.Where(item => Path.GetExtension(item.Value)
            .Equals(".wav", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (wav.Length == 0)
        {
            return resolved;
        }

        var projectRoot = Path.Combine(root, "HbkWwiseEncoder");
        var project = Path.Combine(projectRoot, "HbkWwiseEncoder.wproj");
        var output = Path.Combine(root, "Converted");

        Directory.CreateDirectory(root);
        await RepakArchive.RunAsync(
            FindTool(wwiseConsolePath),
            ["create-new-project", project, "--platform", "Windows", "--quiet"],
            cancellationToken);

        var sourceFile = Path.Combine(projectRoot, "HbkWwise.wsources");
        new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("ExternalSourcesList",
                new XAttribute("SchemaVersion", 1),
                wav.Select(item => new XElement("Source",
                    new XAttribute("Path", item.Value),
                    new XAttribute("Destination", $"{item.Key}.wem"),
                    new XAttribute("Conversion", Conversion)))))
            .Save(sourceFile);
        await RepakArchive.RunAsync(
            FindTool(wwiseConsolePath),
            [
                "convert-external-source", project,
                "--source-file", sourceFile,
                "--platform", "Windows",
                "--output", output,
                "--no-wwise-dat",
                "--quiet"
            ],
            cancellationToken);

        foreach (var item in wav)
        {
            var expected = Path.Combine(output, "Windows", $"{item.Key}.wem");
            var generated = File.Exists(expected)
                ? expected
                : Directory.EnumerateFiles(output, $"{item.Key}.wem", SearchOption.AllDirectories).SingleOrDefault();

            resolved[item.Key] = generated
                ?? throw new InvalidOperationException($"Wwise completed without producing {item.Key}.wem.");
        }

        return resolved;
    }

    public static string FindTool(string? configuredPath = null)
    {
        try
        {
            return RepakArchive.FindTool(
                configuredPath,
                "HBKWWISE_WWISE_CONSOLE",
                "WwiseConsole.exe");
        }
        catch (FileNotFoundException)
        {
            var roots = new[]
            {
                Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Audiokinetic"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Audiokinetic"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Audiokinetic")
            };
            var match = roots.Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateDirectories(root, "Wwise*2019.2*"))
                .Select(version => Path.Combine(version, "Authoring", "x64", "Release", "bin", "WwiseConsole.exe"))
                .FirstOrDefault(File.Exists);

            return match is null
                ? throw new FileNotFoundException(
                    "WwiseConsole.exe from Wwise 2019.2 was not found. Configure its location or set HBKWWISE_WWISE_CONSOLE.")
                : Path.GetFullPath(match);
        }
    }

    private static string ExistingSource(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException("Replacement audio source was not found.", fullPath);
    }

    private static bool SupportedExtension(string extension) =>
        extension.Equals(".wem", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
        || IsCompressed(extension);

    private static bool IsCompressed(string extension) =>
        extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
}
