using System.Text.Json;
using HbkWwise.Core;

namespace HbkWwise.Gui;

public sealed class GuiSettings
{
    public string PakDirectory { get; set; } =
        @"C:\Program Files (x86)\Steam\steamapps\common\Hi-Fi RUSH\Hibiki\Content\Paks";

    public string? RepakPath { get; set; }
    public string? WwiserPath { get; set; }
    public string? PythonPath { get; set; }
    public string? VgmstreamPath { get; set; }
    public string? WwiseConsolePath { get; set; }
    public string? AesKey { get; set; }
    public string? RecentProjectPath { get; set; }
    public string? IndexSourceFingerprint { get; set; }
    public double MasterVolume { get; set; } = 1;
    public bool MetronomeEnabled { get; set; }
    public bool GameSetupCompleted { get; set; }

    public GuiSettings Copy() => new()
    {
        PakDirectory = PakDirectory,
        RepakPath = RepakPath,
        WwiserPath = WwiserPath,
        PythonPath = PythonPath,
        VgmstreamPath = VgmstreamPath,
        WwiseConsolePath = WwiseConsolePath,
        AesKey = AesKey,
        RecentProjectPath = RecentProjectPath,
        IndexSourceFingerprint = IndexSourceFingerprint,
        MasterVolume = MasterVolume,
        MetronomeEnabled = MetronomeEnabled,
        GameSetupCompleted = GameSetupCompleted
    };

    public void CopyFrom(GuiSettings source)
    {
        PakDirectory = source.PakDirectory;
        RepakPath = source.RepakPath;
        WwiserPath = source.WwiserPath;
        PythonPath = source.PythonPath;
        VgmstreamPath = source.VgmstreamPath;
        WwiseConsolePath = source.WwiseConsolePath;
        AesKey = source.AesKey;
        RecentProjectPath = source.RecentProjectPath;
        IndexSourceFingerprint = source.IndexSourceFingerprint;
        MasterVolume = source.MasterVolume;
        MetronomeEnabled = source.MetronomeEnabled;
        GameSetupCompleted = source.GameSetupCompleted;
    }
}

public static class GuiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HbkWwise",
        "settings.json");

    public static GuiSettings Load()
    {
        GuiSettings settings;
        try
        {
            settings = File.Exists(Path)
                ? JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(Path)) ?? new GuiSettings()
                : new GuiSettings();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            settings = new GuiSettings();
        }

        GuiSettingsDiscovery.Populate(settings);
        return settings;
    }

    public static void Save(GuiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var output = System.IO.Path.GetFullPath(Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);

        var temporary = $"{output}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, output, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

public static class GuiSettingsDiscovery
{
    public static void Populate(GuiSettings settings)
    {
        settings.MasterVolume = double.IsFinite(settings.MasterVolume)
            ? Math.Clamp(settings.MasterVolume, 0, 1)
            : 1;

        settings.PakDirectory = ExistingDirectory(Environment.GetEnvironmentVariable("HBKWWISE_PAK_DIR"))
            ?? ExistingDirectory(settings.PakDirectory)
            ?? new GuiSettings().PakDirectory;
        settings.RepakPath = ExistingFile(Environment.GetEnvironmentVariable("HBKWWISE_REPAK"))
            ?? ExistingFile(settings.RepakPath)
            ?? Try(() => RepakArchive.FindTool(null, "HBKWWISE_REPAK", "repak.exe"));
        settings.WwiserPath = ExistingFile(Environment.GetEnvironmentVariable("HBKWWISE_WWISER"))
            ?? ExistingFile(settings.WwiserPath)
            ?? Try(() => WwiserClient.FindWwiser());
        settings.PythonPath = ExistingFile(Environment.GetEnvironmentVariable("HBKWWISE_PYTHON"))
            ?? ExistingFile(settings.PythonPath)
            ?? Try(() => WwiserClient.FindPython());
        settings.VgmstreamPath = ExistingFile(Environment.GetEnvironmentVariable("HBKWWISE_VGMSTREAM"))
            ?? ExistingFile(settings.VgmstreamPath)
            ?? Try(() => VgmstreamClient.FindTool());
        settings.WwiseConsolePath = ExistingFile(Environment.GetEnvironmentVariable("HBKWWISE_WWISE_CONSOLE"))
            ?? ExistingFile(settings.WwiseConsolePath)
            ?? Try(() => WwiseSourceConverter.FindTool());
    }

    private static string? ExistingFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? System.IO.Path.GetFullPath(path)
            : null;

    private static string? ExistingDirectory(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            ? System.IO.Path.GetFullPath(path)
            : null;

    private static string? Try(Func<string> find)
    {
        try
        {
            return find();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
