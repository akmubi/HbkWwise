namespace HbkWwise.Core;

public sealed record ProjectModPakBuildResult(
    string OutputPath,
    string[] Entries,
    string[] Banks,
    ScopedModPakBuildResult[] Compositions,
    ModPakBuildResult? DirectMedia);

public static class ProjectModPakBuilder
{
    public static async Task<ProjectModPakBuildResult> BuildAsync(
        WwiseIndex index,
        IReadOnlyCollection<ScopedModPakRequest> compositions,
        IReadOnlyDictionary<uint, string> directMedia,
        string outputPath,
        string? repakPath = null,
        string? aesKey = null,
        string? wwiserPath = null,
        string? pythonPath = null,
        string? namesPath = null,
        string? vgmstreamPath = null,
        string? wwiseConsolePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(compositions);
        ArgumentNullException.ThrowIfNull(directMedia);
        if (compositions.Count == 0 && directMedia.Count == 0)
        {
            throw new ArgumentException("At least one project edit is required.", nameof(compositions));
        }

        var output = Path.GetFullPath(outputPath);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hbkwwise-project-{Guid.NewGuid():N}");
        var staging = Path.Combine(temporaryRoot, "staging");
        var workingIndex = index;
        var compositionResults = new List<ScopedModPakBuildResult>();
        var importedMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ModPakBuildResult? directResult = null;
        Directory.CreateDirectory(staging);
        try
        {
            var sequence = 0;
            foreach (var request in compositions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bankName = RequestBank(workingIndex, request);
                var effectiveRequest = request with
                {
                    Replacements = request.Replacements.Select(replacement => replacement with
                    {
                        ReuseExistingMedia = importedMedia.Contains($"{bankName}\0{replacement.NewMediaId}")
                    }).ToArray()
                };
                var intermediate = Path.Combine(temporaryRoot, $"composition-{sequence++:D4}.pak");
                var result = await ScopedModPakBuilder.BuildAsync(
                    workingIndex,
                    effectiveRequest,
                    intermediate,
                    repakPath,
                    aesKey,
                    wwiserPath,
                    pythonPath,
                    namesPath,
                    vgmstreamPath,
                    wwiseConsolePath,
                    cancellationToken);
                compositionResults.Add(result);
                importedMedia.UnionWith(result.Imports.Select(imported => $"{result.Bank}\0{imported.NewMediaId}"));
                await OverlayPakAsync(intermediate, workingIndex, result.Bank, staging, repakPath, cancellationToken);
                workingIndex = AddBankOverlay(workingIndex, result.Bank, intermediate);
            }

            if (directMedia.Count > 0)
            {
                var intermediate = Path.Combine(temporaryRoot, $"direct-{sequence:D4}.pak");
                directResult = await ModPakBuilder.BuildAsync(
                    workingIndex,
                    directMedia,
                    intermediate,
                    repakPath,
                    aesKey,
                    wwiserPath,
                    pythonPath,
                    namesPath,
                    vgmstreamPath: vgmstreamPath,
                    cancellationToken: cancellationToken);
                await OverlayPakAsync(intermediate, workingIndex, null, staging, repakPath, cancellationToken);
            }

            var expected = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Select(path => NormalizeEntry(Path.GetRelativePath(staging, path)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var candidate = Path.Combine(
                Path.GetDirectoryName(output)!,
                $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.verify.pak");
            try
            {
                await RepakArchive.PackAsync(staging, candidate, repakPath, cancellationToken);
                var actual = (await RepakArchive.ListAsync(candidate, repakPath, cancellationToken: cancellationToken))
                    .Select(NormalizeEntry)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Generated project PAK entry verification failed.");
                }

                File.Move(candidate, output, true);
                return new ProjectModPakBuildResult(
                    output,
                    actual,
                    compositionResults.Select(result => result.Bank)
                        .Concat(directResult?.Banks ?? [])
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    compositionResults.ToArray(),
                    directResult);
            }
            finally
            {
                File.Delete(candidate);
            }
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, true);
            }
        }
    }

    private static async Task OverlayPakAsync(
        string pakPath,
        WwiseIndex index,
        string? bankName,
        string staging,
        string? repakPath,
        CancellationToken cancellationToken)
    {
        var root = bankName is null
            ? index.Paks?.FirstOrDefault()?.WwiseRoot ?? string.Empty
            : OwnerRoot(index, bankName);
        var pak = new PakSource(pakPath, root, int.MaxValue);
        foreach (var entry in await RepakArchive.ListAsync(pakPath, repakPath, cancellationToken: cancellationToken))
        {
            var normalized = NormalizeEntry(entry);
            var destination = StagePath(staging, normalized);
            await RepakArchive.ExtractEntryAsync(
                [pak], normalized, destination, repakPath, cancellationToken: cancellationToken);
        }
    }

    private static string RequestBank(WwiseIndex index, ScopedModPakRequest request)
    {
        var byId = uint.TryParse(request.Event, out var eventId);
        var matches = index.Events.Where(item => byId
            ? item.Id == eventId
            : item.Name.Equals(request.Event, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1
            ? matches[0].Bank
            : throw new InvalidDataException($"Event selector '{request.Event}' does not identify one SoundBank.");
    }

    private static WwiseIndex AddBankOverlay(WwiseIndex index, string bankName, string pakPath)
    {
        var bank = index.Banks.Single(item => item.Name.Equals(bankName, StringComparison.OrdinalIgnoreCase));
        var effective = bank.EffectiveAsset()
            ?? throw new InvalidDataException($"Bank {bankName} has no effective source asset.");
        var priority = Math.Max(
            index.Paks?.Select(pak => pak.Priority).DefaultIfEmpty(0).Max() ?? 0,
            bank.Assets?.Select(asset => asset.Priority).DefaultIfEmpty(0).Max() ?? 0) + 1;
        var assets = (bank.Assets ?? [])
            .Select(asset => asset with { IsEffective = false })
            .Append(new PakAsset(pakPath, effective.EntryPath, priority, true))
            .ToArray();
        var banks = index.Banks.Select(item => ReferenceEquals(item, bank)
            ? item with { Assets = assets }
            : item).ToArray();
        var paks = (index.Paks ?? [])
            .Append(new PakSource(pakPath, OwnerRoot(index, bankName), priority))
            .ToArray();
        return index with { Banks = banks, Paks = paks };
    }

    private static string OwnerRoot(WwiseIndex index, string bankName)
    {
        var asset = index.Banks.Single(item => item.Name.Equals(bankName, StringComparison.OrdinalIgnoreCase))
            .EffectiveAsset()
            ?? throw new InvalidDataException($"Bank {bankName} has no effective source asset.");
        return index.Paks?.FirstOrDefault(pak => Path.GetFullPath(pak.Path)
            .Equals(Path.GetFullPath(asset.PakPath), StringComparison.OrdinalIgnoreCase))?.WwiseRoot
            ?? Path.GetDirectoryName(asset.EntryPath)?.Replace('\\', '/')
            ?? string.Empty;
    }

    private static string StagePath(string stagingRoot, string entryPath)
    {
        var normalized = NormalizeEntry(entryPath);
        if (normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe PAK entry path '{entryPath}'.");
        }

        var root = Path.GetFullPath(stagingRoot);
        var path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe PAK entry path '{entryPath}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string NormalizeEntry(string value) => value.Replace('\\', '/').TrimStart('/');
}
