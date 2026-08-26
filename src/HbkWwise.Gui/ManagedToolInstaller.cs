using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HbkWwise.Gui;

public sealed record ManagedToolPaths(string WwiserPath, string VgmstreamPath);

public static class ManagedToolInstaller
{
    public const string WwiserVersion = "v20260808";
    public const string VgmstreamVersion = "r2117";

    private const string WwiserUrl =
        "https://github.com/bnnm/wwiser/releases/download/v20260808/wwiser.pyz";
    private const string WwiserSha256 =
        "F4BA1368895ADAB285F27FA37B142E5DA805BCC6FB77E8DABA282DAC65D89411";

    private const string VgmstreamUrl =
        "https://github.com/vgmstream/vgmstream/releases/download/r2117/vgmstream-win64.zip";
    private const string VgmstreamSha256 =
        "6C4A8A3813864FEFED081BBD337DBC0AD93BF88E0B92F5DB98D7AB258B22DC6C";

    private static readonly HttpClient Client = CreateClient();

    public static async Task<ManagedToolPaths> EnsureAsync(
        GuiSettings settings,
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var wwiser = ExistingFile(settings.WwiserPath);
        if (wwiser is null)
        {
            status?.Invoke($"Installing wwiser {WwiserVersion}");
            wwiser = await EnsureWwiserAsync(cancellationToken);
        }

        var vgmstream = ExistingFile(settings.VgmstreamPath);
        if (vgmstream is null)
        {
            status?.Invoke($"Installing vgmstream {VgmstreamVersion}");
            vgmstream = await EnsureVgmstreamAsync(cancellationToken);
        }

        return new ManagedToolPaths(wwiser, vgmstream);
    }

    private static async Task<string> EnsureWwiserAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(GuiPaths.ToolsDirectory, "wwiser", WwiserVersion);
        var path = Path.Combine(directory, "wwiser.pyz");

        if (File.Exists(path) && FileHash(path).Equals(WwiserSha256, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        Directory.CreateDirectory(directory);
        await DownloadVerifiedAsync(WwiserUrl, WwiserSha256, path, cancellationToken);
        return path;
    }

    private static async Task<string> EnsureVgmstreamAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(GuiPaths.ToolsDirectory, "vgmstream", VgmstreamVersion);
        var marker = Path.Combine(directory, ".hbkwwise-managed-tool");
        var executable = Directory.Exists(directory)
            ? Directory.EnumerateFiles(
                    directory,
                    "vgmstream-cli.exe",
                    SearchOption.AllDirectories)
                .FirstOrDefault()
            : null;

        if (executable is not null
            && File.Exists(marker)
            && File.ReadAllText(marker).Trim().Equals(VgmstreamSha256, StringComparison.OrdinalIgnoreCase))
        {
            return executable;
        }

        var downloadDirectory = Path.Combine(GuiPaths.ToolsDirectory, ".downloads");
        Directory.CreateDirectory(downloadDirectory);

        var archive = Path.Combine(downloadDirectory, $"vgmstream-{VgmstreamVersion}.zip");
        var temporary = $"{directory}.{Guid.NewGuid():N}.tmp";
        try
        {
            await DownloadVerifiedAsync(VgmstreamUrl, VgmstreamSha256, archive, cancellationToken);

            Directory.CreateDirectory(temporary);
            ZipFile.ExtractToDirectory(archive, temporary, overwriteFiles: true);

            executable = Directory.EnumerateFiles(
                    temporary,
                    "vgmstream-cli.exe",
                    SearchOption.AllDirectories)
                .FirstOrDefault()
                ?? throw new InvalidDataException(
                    "The downloaded vgmstream archive does not contain vgmstream-cli.exe.");

            File.WriteAllText(
                Path.Combine(temporary, ".hbkwwise-managed-tool"),
                VgmstreamSha256);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            Directory.Move(temporary, directory);

            var relativeExecutable = Path.GetRelativePath(temporary, executable);
            return Path.Combine(directory, relativeExecutable);
        }
        finally
        {
            File.Delete(archive);
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, true);
            }
        }
    }

    private static async Task DownloadVerifiedAsync(
        string url,
        string expectedSha256,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var temporary = $"{output}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var response = await Client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var destination = File.Create(temporary))
            {
                await response.Content.CopyToAsync(destination, cancellationToken);
            }

            var actualSha256 = FileHash(temporary);
            if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Downloaded tool checksum mismatch. Expected {expectedSha256}, got {actualSha256}.");
            }

            File.Move(temporary, output, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? ExistingFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? Path.GetFullPath(path)
            : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("HbkWwise", "1.0"));
        return client;
    }
}
