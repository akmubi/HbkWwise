using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HbkWwise.Core;

public static class RepakArchive
{
    public const string OodleRuntimeFileName = "oo2core_9_win64.dll";

    private static readonly object DegradedReadToolsLock = new();
    private static readonly HashSet<string> DegradedReadTools = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<WwiseIndex> BuildIndexAsync(
        IReadOnlyList<string> pakPaths,
        string cacheRoot,
        string? repakPath = null,
        string? aesKey = null,
        CancellationToken cancellationToken = default)
    {
        if (pakPaths.Count == 0)
        {
            throw new ArgumentException("At least one PAK is required.", nameof(pakPaths));
        }

        var paks = pakPaths.Select(path => ExistingFile(path, "PAK")).ToArray();
        var setStamp = string.Join('\n', paks.Select(path =>
        {
            var info = new FileInfo(path);
            return $"{path}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}";
        }));
        var setKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(setStamp)))[..16];
        var setCache = Path.Combine(Path.GetFullPath(cacheRoot), setKey);

        return await ReadWithFallbackAsync(repakPath, async repak =>
        {
            var prepared = new List<PreparedPak>();
            for (var priority = 0; priority < paks.Length; priority++)
            {
                prepared.Add(await PrepareSourceAsync(
                    paks[priority],
                    setCache,
                    repak,
                    aesKey,
                    priority,
                    cancellationToken));
            }

            var index = new GeneratedSoundBankIndexer().BuildOverlay(
                prepared.Select(item => item.Cache).ToArray());
            return AddPakOwnership(
                index with { Paks = prepared.Select(item => item.Source).ToArray() },
                prepared);
        });
    }

    private static async Task<PreparedPak> PrepareSourceAsync(
        string pak,
        string cacheRoot,
        string repak,
        string? aesKey,
        int priority,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(pak);
        var stamp = $"{pak}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..16];
        var cache = Path.Combine(Path.GetFullPath(cacheRoot), key);
        var infoXml = Directory.Exists(cache)
            ? Directory.EnumerateFiles(cache, "SoundbanksInfo.xml", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (infoXml is null)
        {
            Directory.CreateDirectory(cache);
            await RunAsync(
                repak,
                KeyArgs(aesKey, "unpack", pak, "-o", cache, "-q", "-f", "-i", "**/*.xml"),
                cancellationToken);
            infoXml = Directory.EnumerateFiles(
                    cache,
                    "SoundbanksInfo.xml",
                    SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        if (infoXml is null)
        {
            throw new InvalidDataException("The PAK contains no generated SoundbanksInfo.xml.");
        }

        var entriesPath = Path.Combine(cache, "entries.txt");
        var entries = File.Exists(entriesPath)
            ? await File.ReadAllLinesAsync(entriesPath, cancellationToken)
            : SplitLines(await CaptureAsync(
                repak,
                KeyArgs(aesKey, "list", pak),
                cancellationToken));

        if (!File.Exists(entriesPath))
        {
            await File.WriteAllLinesAsync(entriesPath, entries, cancellationToken);
        }

        var root = Path.GetRelativePath(cache, Path.GetDirectoryName(infoXml)!).Replace('\\', '/');
        return new PreparedPak(
            new PakSource(pak, root, priority),
            cache,
            entries.Select(NormalizeEntry).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public static Task ExtractMediaAsync(
        IReadOnlyList<PakSource> paks,
        uint mediaId,
        string outputPath,
        string? repakPath = null,
        string? aesKey = null,
        CancellationToken cancellationToken = default) =>
        ExtractAsync(paks, $"{mediaId}.wem", outputPath, repakPath, aesKey, cancellationToken);

    public static Task ExtractBankAsync(
        IReadOnlyList<PakSource> paks,
        string bankName,
        string outputPath,
        string? repakPath = null,
        string? aesKey = null,
        CancellationToken cancellationToken = default) =>
        ExtractAsync(
            paks,
            Path.GetExtension(bankName).Equals(".bnk", StringComparison.OrdinalIgnoreCase)
                ? bankName
                : $"{bankName}.bnk",
            outputPath,
            repakPath,
            aesKey,
            cancellationToken);

    public static Task ExtractEntryAsync(
        IReadOnlyList<PakSource> paks,
        string entryPath,
        string outputPath,
        string? repakPath = null,
        string? aesKey = null,
        CancellationToken cancellationToken = default) =>
        ExtractAsync(paks, entryPath, outputPath, repakPath, aesKey, cancellationToken);

    public static async Task<string[]> ListAsync(
        string pakPath,
        string? repakPath = null,
        string? aesKey = null,
        CancellationToken cancellationToken = default)
    {
        var pak = ExistingFile(pakPath, "PAK");
        return await ReadWithFallbackAsync(
            repakPath,
            async repak => SplitLines(await CaptureAsync(
                repak,
                KeyArgs(aesKey, "list", pak),
                cancellationToken)));
    }

    public static async Task PackAsync(
        string stagingDirectory,
        string outputPath,
        string? repakPath = null,
        CancellationToken cancellationToken = default)
    {
        var repak = FindTool(repakPath, "HBKWWISE_REPAK", "repak.exe");
        var staging = Path.GetFullPath(stagingDirectory);

        if (!Directory.Exists(staging))
        {
            throw new DirectoryNotFoundException($"Staging directory not found: {staging}");
        }

        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var temporary = Path.Combine(
            Path.GetDirectoryName(output)!,
            $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp.pak");
        try
        {
            await RunAsync(
                repak,
                ["pack", staging, temporary, "--version", "V11", "--mount-point", "../../../", "-q"],
                cancellationToken);

            if (!File.Exists(temporary))
            {
                throw new InvalidOperationException("repak completed without creating the output PAK.");
            }

            File.Move(temporary, output, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static string EntryPath(PakSource pak, string fileName) =>
        NormalizeEntry(fileName).StartsWith(
            $"{pak.WwiseRoot.TrimEnd('/')}/",
            StringComparison.OrdinalIgnoreCase)
                ? NormalizeEntry(fileName)
                : $"{pak.WwiseRoot.TrimEnd('/')}/{NormalizeEntry(fileName)}";

    internal static string[] MediaEntryCandidates(MediaRecord media)
    {
        var fileName = $"{media.Id}.wem";
        return string.IsNullOrWhiteSpace(media.Language)
            || media.Language.Equals("SFX", StringComparison.OrdinalIgnoreCase)
                ? [fileName]
                : [$"{media.Language.Replace('\\', '/').Trim('/')}/{fileName}", fileName];
    }

    private static async Task ExtractAsync(
        IReadOnlyList<PakSource> paks,
        string fileName,
        string outputPath,
        string? repakPath,
        string? aesKey,
        CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        await ReadWithFallbackAsync(
            repakPath,
            repak => ExtractWithToolAsync(
                repak,
                paks,
                fileName,
                output,
                aesKey,
                cancellationToken));
    }

    private static async Task ExtractWithToolAsync(
        string repak,
        IReadOnlyList<PakSource> paks,
        string fileName,
        string output,
        string? aesKey,
        CancellationToken cancellationToken)
    {
        var temporary = $"{output}.{Guid.NewGuid():N}.tmp";
        InvalidOperationException? missing = null;
        var ordered = paks
            .Select((pak, index) => (Pak: pak, Index: index))
            .OrderByDescending(item => item.Pak.Priority)
            .ThenByDescending(item => item.Index);

        foreach (var item in ordered)
        {
            try
            {
                var entry = EntryPath(item.Pak, fileName);
                try
                {
                    await GetAsync(
                        repak,
                        KeyArgs(aesKey, "get", item.Pak.Path, entry),
                        temporary,
                        cancellationToken);
                }
                catch (RepakProcessException exception) when (exception.Panicked)
                {
                    await UnpackEntryAsync(
                        repak,
                        item.Pak.Path,
                        entry,
                        temporary,
                        aesKey,
                        cancellationToken);
                }

                File.Move(temporary, output, true);
                return;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("No entry found", StringComparison.OrdinalIgnoreCase))
            {
                missing = exception;
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        throw missing ?? new InvalidOperationException($"No indexed PAK contains {fileName}.");
    }

    private static async Task ReadWithFallbackAsync(string? configuredPath, Func<string, Task> read) =>
        _ = await ReadWithFallbackAsync(configuredPath, async repak =>
        {
            await read(repak);
            return true;
        });

    private static async Task<T> ReadWithFallbackAsync<T>(
        string? configuredPath,
        Func<string, Task<T>> read)
    {
        var tools = ReadTools(configuredPath);
        var failures = new List<(string Tool, Exception Error)>();

        foreach (var tool in tools)
        {
            try
            {
                var result = await read(tool);
                if (failures.Count > 0)
                {
                    lock (DegradedReadToolsLock)
                    {
                        DegradedReadTools.UnionWith(failures.Select(failure => failure.Tool));
                    }
                }

                return result;
            }
            catch (Exception exception) when (IsArchiveLayoutFailure(exception))
            {
                failures.Add((tool, exception));
            }
        }

        var message = tools.Length == 1
            ? $"repak could not read the selected encrypted PAK entry ({tools[0]}). Verify the AES key; this build may not support the archive layout."
            : $"The selected repak build and its bundled compatible fallback could not read the archive. Verify the AES key. Tried: {string.Join("; ", tools)}.";
        var errors = failures.Select(failure => failure.Error).ToArray();

        throw new InvalidDataException(
            message,
            errors.Length == 1 ? errors[0] : new AggregateException(errors));
    }

    private static string[] ReadTools(string? configuredPath)
    {
        var primary = FindTool(configuredPath, "HBKWWISE_REPAK", "repak.exe");
        var tools = new[]
        {
            primary,
            Path.Combine(Environment.CurrentDirectory, "tools", "win-x64", "repak.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "win-x64", "repak.exe")
        }
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (DegradedReadToolsLock)
        {
            return tools.OrderBy(DegradedReadTools.Contains).ToArray();
        }
    }

    private static bool IsArchiveLayoutFailure(Exception exception) => exception switch
    {
        RepakProcessException { Panicked: true } => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsArchiveLayoutFailure),
        { InnerException: not null } => IsArchiveLayoutFailure(exception.InnerException),
        _ => false
    };

    private static async Task UnpackEntryAsync(
        string repak,
        string pak,
        string entry,
        string output,
        string? aesKey,
        CancellationToken cancellationToken)
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "hbkwwise-repak",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            await RunAsync(
                repak,
                KeyArgs(
                    aesKey,
                    "unpack",
                    pak,
                    "-o",
                    temporaryDirectory,
                    "-q",
                    "-f",
                    "-i",
                    entry),
                cancellationToken);

            var expected = Path.Combine(
                temporaryDirectory,
                entry.Replace('/', Path.DirectorySeparatorChar));
            var extracted = File.Exists(expected)
                ? expected
                : Directory.EnumerateFiles(
                        temporaryDirectory,
                        Path.GetFileName(entry),
                        SearchOption.AllDirectories)
                    .FirstOrDefault(path => NormalizeEntry(
                            Path.GetRelativePath(temporaryDirectory, path))
                        .EndsWith(NormalizeEntry(entry), StringComparison.OrdinalIgnoreCase));

            if (extracted is null)
            {
                throw new InvalidOperationException(
                    $"repak unpack completed without extracting {entry}.");
            }

            File.Move(extracted, output, true);
        }
        catch (RepakProcessException exception) when (exception.Panicked)
        {
            throw new InvalidDataException(
                "repak encountered the archive-layout failure during both direct and filtered extraction.",
                exception);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }
    }

    internal static async Task RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        using var process = Start(tool, arguments, workingDirectory);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        await WaitForExitAsync(process, cancellationToken);
        var error = await stderr;
        _ = await stdout;

        CheckExit(process, error);
    }

    internal static async Task<string> CaptureAsync(
        string tool,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = Start(tool, arguments);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        await WaitForExitAsync(process, cancellationToken);
        var output = await stdout;
        var error = await stderr;

        CheckExit(process, error);
        return output;
    }

    private static async Task GetAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string output,
        CancellationToken cancellationToken)
    {
        using var process = Start(tool, arguments);
        await using var destination = File.Create(output);

        var copy = process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var exit = WaitForExitAsync(process, cancellationToken);

        await Task.WhenAll(copy, exit);
        CheckExit(process, await stderr);
    }

    private static async Task WaitForExitAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);

            if (!process.HasExited)
            {
                await process.WaitForExitAsync();
            }

            throw;
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill.
        }
        catch (Win32Exception)
        {
            // Preserve cancellation if the process has already become unavailable.
        }
    }

    private static Process Start(
        string tool,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(tool)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(tool)}.");
    }

    private static void CheckExit(Process process, string error)
    {
        if (process.ExitCode == 0)
        {
            return;
        }

        var message = string.IsNullOrWhiteSpace(error)
            ? $"{Path.GetFileName(process.StartInfo.FileName)} failed with exit code {process.ExitCode}."
            : error.Trim();
        throw new RepakProcessException(
            message,
            message.Contains("panicked at", StringComparison.OrdinalIgnoreCase)
            || message.Contains("index out of bounds", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] KeyArgs(string? aesKey, params string[] arguments) =>
        string.IsNullOrWhiteSpace(aesKey)
            ? arguments
            : ["--aes-key", aesKey, .. arguments];

    private static WwiseIndex AddPakOwnership(WwiseIndex index, IReadOnlyList<PreparedPak> paks)
    {
        var bankPaths = index.Banks
            .GroupBy(bank => bank.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(bank => bank.Path)
                    .FirstOrDefault(path => path.Length > 0)
                    ?? $"{group.Key}.bnk",
                StringComparer.OrdinalIgnoreCase);

        return index with
        {
            Banks = index.Banks.Select(bank => bank with
            {
                Assets = FindAssets(paks, bankPaths[bank.Name])
            }).ToArray(),
            Media = index.Media.Select(media => media with
            {
                Assets = FindAssets(
                    paks,
                    media.IsStreamed
                        ? MediaEntryCandidates(media)
                        : [bankPaths.GetValueOrDefault(media.Bank, $"{media.Bank}.bnk")])
            }).ToArray()
        };
    }

    private static PakAsset[] FindAssets(IReadOnlyList<PreparedPak> paks, string fileName) =>
        FindAssets(paks, [fileName]);

    private static PakAsset[] FindAssets(IReadOnlyList<PreparedPak> paks, IReadOnlyCollection<string> fileNames)
    {
        var candidates = paks.SelectMany(pak => fileNames.Select(fileName => new
        {
            Pak = pak,
            Entry = EntryPath(pak.Source, fileName)
        }))
            .Where(item => item.Pak.Entries.Contains(item.Entry))
            .DistinctBy(item => $"{item.Pak.Source.Path}\0{item.Entry}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Pak.Source.Priority)
            .ToArray();

        if (candidates.Length == 0)
        {
            return [];
        }

        var effectivePriority = candidates[^1].Pak.Source.Priority;
        return candidates.Select(item => new PakAsset(
            item.Pak.Source.Path,
            item.Entry,
            item.Pak.Source.Priority,
            item.Pak.Source.Priority == effectivePriority)).ToArray();
    }

    private static string[] SplitLines(string value) => value
        .Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeEntry(string value) =>
        value.Replace('\\', '/').TrimStart('/');

    private static string ExistingFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            ? fullPath
            : throw new FileNotFoundException($"{description} file not found.", fullPath);
    }

    public static string FindTool(
        string? configuredPath,
        string environmentVariable,
        string fileName)
    {
        var installedCandidates = fileName.Equals("repak.exe", StringComparison.OrdinalIgnoreCase)
            ? new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "repak_cli",
                    "bin",
                    fileName),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "repak_cli",
                    "bin",
                    fileName)
            }
            : [];
        var candidates = new[]
        {
            configuredPath,
            Environment.GetEnvironmentVariable(environmentVariable)
        }
            .Concat(installedCandidates)
            .Concat(new[]
            {
                Path.Combine(Environment.CurrentDirectory, "tools", "win-x64", fileName),
                Path.Combine(AppContext.BaseDirectory, "tools", "win-x64", fileName),
                Path.Combine(AppContext.BaseDirectory, fileName)
            })
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(path => Path.Combine(path.Trim('"'), fileName)));

        var found = candidates.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        return found is null
            ? throw new FileNotFoundException(
                $"{fileName} was not found. Bundle it under tools/win-x64, set {environmentVariable}, or pass its path explicitly.")
            : Path.GetFullPath(found);
    }

    public static string? FindOodleRuntime(string repakPath)
    {
        var candidate = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(repakPath))!,
            OodleRuntimeFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    private sealed record PreparedPak(
        PakSource Source,
        string Cache,
        IReadOnlySet<string> Entries);

    private sealed class RepakProcessException(string message, bool panicked)
        : InvalidOperationException(message)
    {
        public bool Panicked { get; } = panicked;
    }
}
