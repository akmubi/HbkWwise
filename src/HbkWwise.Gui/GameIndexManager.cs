using HbkWwise.Core;

namespace HbkWwise.Gui;

public sealed record PreparedGameIndex(
    WwiseIndex Index,
    string PakDirectory,
    string SourceFingerprint);

public static class GameIndexManager
{
    public static bool TryResolveConfiguration(
        string pakDirectory,
        string? aesKey,
        out string[] pakPaths,
        out string sourceFingerprint)
    {
        pakPaths = [];
        sourceFingerprint = string.Empty;
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            return false;
        }

        try
        {
            pakPaths = GameDataConfiguration.ResolvePakPaths(pakDirectory);
            sourceFingerprint = GameDataConfiguration.Fingerprint(pakPaths, aesKey);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException
                or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static async Task<PreparedGameIndex> BuildAsync(
        string pakDirectory,
        string aesKey,
        string? repakPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pakDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(aesKey);

        var directory = Path.GetFullPath(pakDirectory);
        var pakPaths = GameDataConfiguration.ResolvePakPaths(directory);
        var fingerprint = GameDataConfiguration.Fingerprint(pakPaths, aesKey);
        var cacheDirectory = Path.Combine(
            GuiPaths.IndexCacheDirectory,
            fingerprint);

        Directory.CreateDirectory(GuiPaths.Root);
        Directory.CreateDirectory(cacheDirectory);

        var generated = (await RepakArchive.BuildIndexAsync(
            pakPaths,
            cacheDirectory,
            repakPath,
            aesKey,
            cancellationToken)) with
        {
            SourceFingerprint = fingerprint
        };

        await IndexStore.SaveAsync(generated, GuiPaths.IndexPath, cancellationToken);
        return new PreparedGameIndex(generated, directory, fingerprint);
    }
}
