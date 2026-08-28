using System.Text.Json;

namespace HbkWwise.Core;

public sealed record MediaFormat(
    string DecoderVersion,
    int SampleRate,
    int Channels,
    long Samples,
    string Encoding,
    string Layout,
    int? Bitrate)
{
    public double DurationSeconds => SampleRate == 0 ? 0 : (double)Samples / SampleRate;
}

public static class VgmstreamClient
{
    public static async Task<MediaFormat> InspectAsync(
        string inputPath,
        string? toolPath = null,
        CancellationToken cancellationToken = default)
    {
        var input = ExistingFile(inputPath);
        var output = await RepakArchive.CaptureAsync(FindTool(toolPath), ["-I", input], cancellationToken);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        return new MediaFormat(
            root.GetProperty("version").GetString() ?? string.Empty,
            root.GetProperty("sampleRate").GetInt32(),
            root.GetProperty("channels").GetInt32(),
            root.GetProperty("numberOfSamples").GetInt64(),
            root.GetProperty("encoding").GetString() ?? string.Empty,
            root.GetProperty("layout").GetString() ?? string.Empty,
            root.TryGetProperty("bitrate", out var bitrate) && bitrate.ValueKind == JsonValueKind.Number
                ? bitrate.GetInt32()
                : null);
    }

    public static async Task DecodeAsync(
        string inputPath,
        string outputPath,
        string? toolPath = null,
        CancellationToken cancellationToken = default)
    {
        var input = ExistingFile(inputPath);
        var output = Path.GetFullPath(outputPath);

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = $"{output}.{Guid.NewGuid():N}.tmp.wav";
        try
        {
            await RepakArchive.RunAsync(FindTool(toolPath), ["-i", "-o", temporary, input], cancellationToken);
            File.Move(temporary, output, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static string FindTool(string? configuredPath = null)
    {
        var roots = new[] { Environment.CurrentDirectory, Directory.GetParent(Environment.CurrentDirectory)?.FullName };
        var local = roots.Where(root => root is not null)
            .Select(root => Path.Combine(root!, "vgmstream-win", "vgmstream-cli.exe"))
            .FirstOrDefault(File.Exists);
        var bundled = Path.Combine(
            AppContext.BaseDirectory,
            "tools",
            "win-x64",
            "vgmstream",
            "vgmstream-cli.exe");

        return RepakArchive.FindTool(
            configuredPath ?? local ?? bundled,
            "HBKWWISE_VGMSTREAM",
            "vgmstream-cli.exe");
    }

    private static string ExistingFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath) ? fullPath : throw new FileNotFoundException("Media file not found.", fullPath);
    }
}
