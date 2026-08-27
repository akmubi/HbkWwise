using System.Security.Cryptography;
using System.Text;

namespace HbkWwise.Core;

public static class GameDataConfiguration
{
    public const string BasePakName = "Hibiki-WindowsNoEditor.pak";
    public const string UpdatePakName = "Hibiki-WindowsNoEditor_0_P.pak";
    public const string XboxPakName = "pakchunk0-WinGDK.pak";

    private const string IndexFormatStamp = "hbkwwise-index-v1";

    public static string[] ResolvePakPaths(string pakDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakDirectory);

        var directory = Path.GetFullPath(pakDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"PAK directory not found: {directory}");
        }

        var basePak = Path.Combine(directory, BasePakName);
        if (File.Exists(basePak))
        {
            var updatePak = Path.Combine(directory, UpdatePakName);
            return File.Exists(updatePak) ? [basePak, updatePak] : [basePak];
        }

        var xboxPak = Path.Combine(directory, XboxPakName);
        return File.Exists(xboxPak)
            ? [xboxPak]
            : throw new FileNotFoundException(
                $"No supported Hi-Fi RUSH PAKs were found in {directory}. Expected {BasePakName} or {XboxPakName}.");
    }

    public static string? FindInstalledPakDirectory(IEnumerable<string>? candidates = null)
    {
        foreach (var candidate in (candidates ?? InstallationCandidates())
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _ = ResolvePakPaths(candidate);
                return Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException
                    or IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    public static string Fingerprint(
        IReadOnlyCollection<string> pakPaths,
        string aesKey)
    {
        ArgumentNullException.ThrowIfNull(pakPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(aesKey);
        if (pakPaths.Count == 0)
        {
            throw new ArgumentException("At least one game PAK is required.", nameof(pakPaths));
        }

        var source = new StringBuilder(IndexFormatStamp)
            .Append('\n')
            .Append("aes-key")
            .Append('\0')
            .Append(aesKey.Trim());

        foreach (var path in pakPaths)
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);

            source.Append('\n')
                .Append(fullPath)
                .Append('\0')
                .Append(info.Length)
                .Append('\0')
                .Append(info.LastWriteTimeUtc.Ticks);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }

    private static IEnumerable<string> InstallationCandidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        yield return Path.Combine(
            programFilesX86,
            "Steam", "steamapps", "common", "Hi-Fi RUSH", "Hibiki", "Content", "Paks");
        yield return Path.Combine(
            programFiles,
            "Epic Games", "HiFiRUSH", "Hibiki", "Content", "Paks");

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
        {
            yield return Path.Combine(
                drive.RootDirectory.FullName,
                "SteamLibrary", "steamapps", "common", "Hi-Fi RUSH", "Hibiki", "Content", "Paks");
            yield return Path.Combine(
                drive.RootDirectory.FullName,
                "Epic Games", "HiFiRUSH", "Hibiki", "Content", "Paks");
            yield return Path.Combine(
                drive.RootDirectory.FullName,
                "XboxGames", "Hi-Fi RUSH", "Content", "Hibiki", "Content", "Paks");
        }
    }

}
