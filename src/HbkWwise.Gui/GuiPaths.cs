using System.Security.Cryptography;
using System.Text;

namespace HbkWwise.Gui;

public static class GuiPaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HbkWwise");

    public static string IndexPath => Path.Combine(Root, "index.json");

    public static string IndexCacheDirectory => Path.Combine(Root, "index-cache");

    public static string ToolsDirectory => Path.Combine(Root, "tools");
}

public static class GameDataConfiguration
{
    public const string BasePakName = "Hibiki-WindowsNoEditor.pak";
    public const string UpdatePakName = "Hibiki-WindowsNoEditor_0_P.pak";

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
        if (!File.Exists(basePak))
        {
            throw new FileNotFoundException("Base game PAK not found.", basePak);
        }

        var updatePak = Path.Combine(directory, UpdatePakName);
        return File.Exists(updatePak)
            ? [basePak, updatePak]
            : [basePak];
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
}
