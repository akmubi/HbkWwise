using System.Buffers.Binary;

namespace HbkWwise.Core;

public sealed record ModPakRetimeRequest(
    string Event,
    uint ScopeObjectId,
    double FromBpm,
    double NewBpm,
    double Epsilon = 0.01);

public sealed record ModPakRetimeResult(
    string Bank,
    uint EventId,
    uint ScopeObjectId,
    uint[] AffectedMediaIds,
    int RetimeObjects,
    int PatchCount);

public sealed record ModPakDurationResult(
    string Bank,
    uint ScopeObjectId,
    BnkClipUsage[] ClipUsages,
    BnkMediaDurationCheck[] Checks);

public sealed record ModPakBuildResult(
    string OutputPath,
    string[] Entries,
    string[] Banks,
    uint[] MediaIds,
    ModPakRetimeResult[] Retimes,
    ModPakDurationResult? DurationValidation,
    BnkTimelineValidation? TimelineValidation);

public static class ModPakBuilder
{
    public static async Task<ModPakBuildResult> BuildAsync(
        WwiseIndex index,
        IReadOnlyDictionary<uint, string> replacements,
        string outputPath,
        string? repakPath = null,
        string? aesKey = null,
        string? wwiserPath = null,
        string? pythonPath = null,
        string? namesPath = null,
        ModPakRetimeRequest? retime = null,
        string? vgmstreamPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0)
        {
            throw new ArgumentException("At least one media replacement is required.", nameof(replacements));
        }

        var paks = index.Paks is { Length: > 0 }
            ? index.Paks
            : throw new InvalidOperationException("The index was not built from PAK files.");
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            throw new InvalidOperationException("An AES key is required to extract source banks from the game PAKs.");
        }

        var output = Path.GetFullPath(outputPath);
        if (paks.Any(pak => Path.GetFullPath(pak.Path).Equals(output, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The mod PAK must not overwrite an indexed game PAK.");
        }

        var replacementBytes = replacements.ToDictionary(
            item => item.Key,
            item => ReadCompleteWem(item.Key, item.Value));
        IReadOnlyDictionary<uint, double>? replacementDurations = null;
        if (retime is not null)
        {
            var inspected = await Task.WhenAll(replacements.Select(async item =>
            {
                var format = await VgmstreamClient.InspectAsync(item.Value, vgmstreamPath, cancellationToken);
                return (item.Key, DurationMs: format.DurationSeconds * 1000);
            }));
            replacementDurations = inspected.ToDictionary(item => item.Key, item => item.DurationMs);
        }

        var bankWork = new Dictionary<string, BankWork>(StringComparer.OrdinalIgnoreCase);
        var externalMedia = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var replacement in replacementBytes)
        {
            var records = index.FindMedia(replacement.Key);
            if (records.Count == 0)
            {
                throw new InvalidOperationException($"Unknown media ID {replacement.Key}.");
            }

            foreach (var media in records)
            {
                if (media.IsStreamed)
                {
                    var asset = media.EffectiveAsset()
                        ?? throw new InvalidOperationException($"No effective external WEM entry was indexed for media {media.Id}.");
                    AddExternal(externalMedia, asset.EntryPath, replacement.Value, media.Id);
                }

                if (!media.IsStreamed || media.PrefetchSize is > 0)
                {
                    var bank = FindBank(index, media);
                    var asset = bank.EffectiveAsset()
                        ?? throw new InvalidOperationException($"No effective PAK entry was indexed for bank '{bank.Name}'.");

                    if (!bankWork.TryGetValue(asset.EntryPath, out var work))
                    {
                        work = bankWork[asset.EntryPath] = new BankWork(bank, asset.EntryPath);
                    }

                    work.Replacements.TryAdd(media.Id, replacement.Value);
                }
            }
        }

        if (retime is not null)
        {
            var byId = uint.TryParse(retime.Event, out var eventId);
            var events = index.Events.Where(item => byId
                    ? item.Id == eventId
                    : item.Name.Equals(retime.Event, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (events.Length != 1)
            {
                throw new InvalidOperationException(events.Length == 0
                    ? $"No exact Event matching '{retime.Event}'."
                    : $"Event selector '{retime.Event}' is ambiguous; use an Event ID.");
            }

            var item = events[0];
            var bank = index.Banks.FirstOrDefault(candidate =>
                candidate.Name.Equals(item.Bank, StringComparison.OrdinalIgnoreCase) && candidate.EffectiveAsset() is not null)
                ?? throw new InvalidOperationException($"Bank '{item.Bank}' for Event {item.Id} is absent from the PAK index.");
            var asset = bank.EffectiveAsset()!;

            if (!bankWork.TryGetValue(asset.EntryPath, out var work))
            {
                work = bankWork[asset.EntryPath] = new BankWork(bank, asset.EntryPath);
            }

            work.RetimeEvent = item;
        }

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hbkwwise-build-{Guid.NewGuid():N}");
        var staging = Path.Combine(temporaryRoot, "staging");

        Directory.CreateDirectory(staging);
        var retimeResults = new List<ModPakRetimeResult>();
        ModPakDurationResult? durationResult = null;
        BnkTimelineValidation? timelineResult = null;
        try
        {
            foreach (var external in externalMedia)
            {
                var staged = StagePath(staging, external.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await File.WriteAllBytesAsync(staged, external.Value, cancellationToken);
            }

            foreach (var work in bankWork.Values)
            {
                var sourceBank = Path.Combine(temporaryRoot, $"{Guid.NewGuid():N}.bnk");
                await RepakArchive.ExtractEntryAsync(paks, work.EntryPath, sourceBank, repakPath, aesKey, cancellationToken);
                var sourceBytes = await File.ReadAllBytesAsync(sourceBank, cancellationToken);
                var bank = BnkFile.Parse(sourceBytes);
                var resized = work.Replacements
                    .Where(item => bank.TryGetMedia(item.Key, out var entry)
                        && entry.Kind == BnkMediaKind.Embedded
                        && item.Value.Length != entry.Size)
                    .Select(item => item.Key)
                    .ToArray();

                WwiserHircGraph? graph = null;
                var xml = string.Empty;
                if (resized.Length > 0 || work.RetimeEvent is not null)
                {
                    xml = Path.Combine(temporaryRoot, $"{Guid.NewGuid():N}.xml");
                    await WwiserClient.DumpXmlAsync(sourceBank, xml, wwiserPath, pythonPath, namesPath, cancellationToken);
                    graph = WwiserHircGraph.Load(xml);
                }

                var timedBytes = sourceBytes;
                if (work.RetimeEvent is not null)
                {
                    var request = retime!;
                    var validation = BnkTimelineValidator.Validate(
                        xml,
                        request.ScopeObjectId,
                        replacementDurations!,
                        request.FromBpm,
                        request.NewBpm,
                        eventNameOrId: work.RetimeEvent.Id.ToString());

                    timelineResult = validation;
                    durationResult = new ModPakDurationResult(
                        work.Bank.Name,
                        validation.DurationValidation.ScopeObjectId,
                        validation.DurationValidation.ClipUsages,
                        validation.DurationValidation.Checks);
                    var errors = validation.Issues.Where(item => item.Severity == BnkTimelineSeverity.Error).ToArray();
                    if (errors.Length > 0)
                    {
                        throw new InvalidDataException("Timeline validation failed: "
                            + string.Join("; ", errors.Take(5).Select(item =>
                                $"{item.Code} object {item.ObjectId}{(item.MediaId is null ? string.Empty : $" media {item.MediaId}")}: {item.Message}")));
                    }

                    var plan = BnkRetimer.Plan(
                        sourceBytes,
                        xml,
                        request.ScopeObjectId,
                        request.NewBpm,
                        request.FromBpm,
                        epsilon: request.Epsilon,
                        eventNameOrId: work.RetimeEvent.Id.ToString());
                    timedBytes = BnkRetimer.Apply(sourceBytes, plan);
                    bank = BnkFile.Parse(timedBytes);
                    retimeResults.Add(new ModPakRetimeResult(
                        work.Bank.Name,
                        work.RetimeEvent.Id,
                        plan.ScopeObjectId,
                        plan.AffectedMediaIds,
                        plan.RetimeObjectIds.Length,
                        plan.Patches.Length));
                }

                var offsets = graph is null ? null : resized.ToDictionary(id => id, id => graph.MemorySizeOffsets(id));
                var rewritten = bank.RewriteMedia(work.Replacements, offsets);

                _ = BnkFile.Parse(rewritten.Data);
                var stagedBank = StagePath(staging, work.EntryPath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedBank)!);
                await File.WriteAllBytesAsync(stagedBank, rewritten.Data, cancellationToken);
                if (resized.Length > 0 || work.RetimeEvent is not null)
                {
                    var validationXml = Path.Combine(temporaryRoot, $"{Guid.NewGuid():N}.xml");
                    await WwiserClient.DumpXmlAsync(stagedBank, validationXml, wwiserPath, pythonPath, namesPath, cancellationToken);
                    _ = WwiserHircGraph.Load(validationXml);
                }
            }

            var expected = bankWork.Keys.Concat(externalMedia.Keys)
                .Select(NormalizeEntry)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var candidate = Path.Combine(
                Path.GetDirectoryName(output)!,
                $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.verify.pak");

            string[] actual;
            try
            {
                await RepakArchive.PackAsync(staging, candidate, repakPath, cancellationToken);
                actual = (await RepakArchive.ListAsync(candidate, repakPath, cancellationToken: cancellationToken))
                    .Select(NormalizeEntry)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (!actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Generated PAK entry verification failed.");
                }

                File.Move(candidate, output, true);
            }
            finally
            {
                File.Delete(candidate);
            }

            return new ModPakBuildResult(
                output,
                actual,
                bankWork.Values.Select(item => item.Bank.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
                replacements.Keys.Order().ToArray(),
                retimeResults.OrderBy(item => item.Bank, StringComparer.OrdinalIgnoreCase).ToArray(),
                durationResult,
                timelineResult);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, true);
            }
        }
    }

    private static BankRecord FindBank(WwiseIndex index, MediaRecord media) => index.Banks
        .Where(bank => bank.Name.Equals(media.Bank, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(bank => bank.Language.Equals(media.Language, StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault()
        ?? throw new InvalidOperationException($"Bank '{media.Bank}' for media {media.Id} is absent from the index.");

    private static byte[] ReadCompleteWem(uint id, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Replacement WEM for media {id} was not found.", fullPath);
        }

        var data = File.ReadAllBytes(fullPath);
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !data.AsSpan(8, 4).SequenceEqual("WAVE"u8)
            || (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) + 8 != data.Length)
        {
            throw new InvalidDataException($"Replacement for media {id} is not a complete RIFF/WAVE WEM.");
        }

        return data;
    }

    private static void AddExternal(Dictionary<string, byte[]> files, string entryPath, byte[] data, uint id)
    {
        var entry = NormalizeEntry(entryPath);
        if (files.TryGetValue(entry, out var existing) && !existing.AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException($"Conflicting replacements target external entry '{entry}' for media {id}.");
        }

        files[entry] = data;
    }

    private static string StagePath(string stagingRoot, string entryPath)
    {
        var relative = NormalizeEntry(entryPath);
        if (relative.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe PAK entry path '{entryPath}'.");
        }

        var root = Path.GetFullPath(stagingRoot);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe PAK entry path '{entryPath}'.");
        }

        return path;
    }

    private static string NormalizeEntry(string value) => value.Replace('\\', '/').TrimStart('/');

    private sealed record BankWork(BankRecord Bank, string EntryPath)
    {
        public Dictionary<uint, byte[]> Replacements { get; } = [];

        public EventRecord? RetimeEvent { get; set; }
    }
}
