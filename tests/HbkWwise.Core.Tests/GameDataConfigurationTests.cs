using HbkWwise.Core;

namespace HbkWwise.Core.Tests;

public sealed class GameDataConfigurationTests
{
    [Fact]
    public void ResolvePakPaths_UsesDesktopBaseThenUpdate()
    {
        using var directory = new TemporaryDirectory();
        var basePak = directory.Touch(GameDataConfiguration.BasePakName);
        var updatePak = directory.Touch(GameDataConfiguration.UpdatePakName);

        Assert.Equal([basePak, updatePak], GameDataConfiguration.ResolvePakPaths(directory.Path));
    }

    [Fact]
    public void ResolvePakPaths_UsesXboxAudioPakAndIgnoresNonAudioChunks()
    {
        using var directory = new TemporaryDirectory();
        var chunk0 = directory.Touch(GameDataConfiguration.XboxPakName);
        _ = directory.Touch("pakchunk1-WinGDK.pak");
        _ = directory.Touch("pakchunk0optional-WinGDK.pak");

        Assert.Equal([chunk0], GameDataConfiguration.ResolvePakPaths(directory.Path));
    }

    [Fact]
    public void FindInstalledPakDirectory_SkipsUnsupportedDirectories()
    {
        using var unsupported = new TemporaryDirectory();
        using var game = new TemporaryDirectory();
        _ = game.Touch(GameDataConfiguration.XboxPakName);

        Assert.Equal(
            game.Path,
            GameDataConfiguration.FindInstalledPakDirectory([unsupported.Path, game.Path]));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"hbkwwise-game-data-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Touch(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, []);
            return path;
        }

        public void Dispose() => Directory.Delete(Path, true);
    }
}
