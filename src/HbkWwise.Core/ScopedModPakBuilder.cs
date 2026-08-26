using System.Buffers.Binary;

namespace HbkWwise.Core;

public sealed record ScopedMediaReplacement(
    uint OldMediaId,
    uint NewMediaId,
    string WemPath,
    bool ReferencesAlreadyUseNewId = false,
    bool ReuseExistingMedia = false);

public sealed record ScopedSegmentTempoChange(uint SegmentObjectId, double FromBpm, double NewBpm);

public sealed record ScopedModPakRequest(
    string Event,
    uint ScopeObjectId,
    double FromBpm,
    double NewBpm,
    ScopedMediaReplacement[] Replacements,
    BnkTimelineClipEdit[]? TimelineEdits = null,
    BnkTrackPlaylistEdit[]? PlaylistEdits = null,
    ScopedSegmentTempoChange[]? SegmentTempos = null,
    BnkTimelineMarkerEdit[]? MarkerEdits = null,
    BnkTimelineSegmentDurationEdit[]? SegmentDurationEdits = null);

public sealed record ScopedMediaImportResult(
    uint OldMediaId,
    uint NewMediaId,
    BnkMediaKind Storage,
    int ReferenceCount,
    string? ExternalEntryPath);

public sealed record ScopedModPakBuildResult(
    string OutputPath,
    string Bank,
    uint EventId,
    uint ScopeObjectId,
    double FromBpm,
    double NewBpm,
    int TimelinePatchCount,
    string[] Entries,
    ScopedMediaImportResult[] Imports,
    BnkTimelineValidation Validation,
    ScopedSegmentTempoChange[]? SegmentTempos = null);

public static class ScopedModPakBuilder
{
    public static async Task<ScopedModPakBuildResult> BuildAsync(
        WwiseIndex index,
        ScopedModPakRequest request,
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
        ArgumentNullException.ThrowIfNull(request);
        var hasTimingEdits = Math.Abs(request.FromBpm - request.NewBpm) > 0.01
            || request.SegmentTempos is { Length: > 0 }
            || request.TimelineEdits is { Length: > 0 }
            || request.PlaylistEdits is { Length: > 0 }
            || request.MarkerEdits is { Length: > 0 }
            || request.SegmentDurationEdits is { Length: > 0 };
        if (request.Replacements.Length == 0 && !hasTimingEdits)
        {
            throw new ArgumentException("At least one media or timing edit is required.", nameof(request));
        }

        if (request.FromBpm <= 0 || request.NewBpm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "BPM values must be positive.");
        }

        var segmentTempos = request.SegmentTempos ?? [];
        if (segmentTempos.Any(item => item.SegmentObjectId == 0 || item.FromBpm <= 0 || item.NewBpm <= 0)
            || segmentTempos.GroupBy(item => item.SegmentObjectId).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Segment tempo changes contain invalid or duplicate Music Segment IDs.");
        }

        if (segmentTempos.Length > 0 && Math.Abs(request.FromBpm - request.NewBpm) > 0.01)
        {
            throw new InvalidDataException("Parent-scope and per-segment tempo changes cannot be combined in one build.");
        }

        var paks = index.Paks is { Length: > 0 }
            ? index.Paks
            : throw new InvalidOperationException("The index was not built from PAK files.");
        if (string.IsNullOrWhiteSpace(aesKey))
        {
            throw new InvalidOperationException("An AES key is required to extract the source bank.");
        }

        var output = Path.GetFullPath(outputPath);
        if (paks.Any(pak => Path.GetFullPath(pak.Path).Equals(output, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The mod PAK must not overwrite an indexed game PAK.");
        }

        var selectedEvent = ResolveEvent(index, request.Event);
        var bank = index.Banks.FirstOrDefault(item =>
            item.Name.Equals(selectedEvent.Bank, StringComparison.OrdinalIgnoreCase) && item.EffectiveAsset() is not null)
            ?? throw new InvalidOperationException($"Bank '{selectedEvent.Bank}' has no effective indexed PAK asset.");
        var bankAsset = bank.EffectiveAsset()!;

        ValidateReplacementIds(index, request.Replacements);

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hbkwwise-scoped-{Guid.NewGuid():N}");
        var sourceBank = Path.Combine(temporaryRoot, "source.bnk");
        var sourceXml = Path.Combine(temporaryRoot, "source.xml");
        var staging = Path.Combine(temporaryRoot, "staging");
        var stagedBank = StagePath(staging, bankAsset.EntryPath);

        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var converted = request.Replacements.Length == 0
                ? new Dictionary<uint, string>()
                : await WwiseSourceConverter.ConvertAsync(
                    request.Replacements.Select(item => new WwiseSourceInput(item.NewMediaId, item.WemPath)).ToArray(),
                    Path.Combine(temporaryRoot, "encoder"),
                    wwiseConsolePath,
                    vgmstreamPath,
                    cancellationToken);
            var replacementBytes = request.Replacements.ToDictionary(
                item => item.NewMediaId,
                item => ReadCompleteWem(item.NewMediaId, converted[item.NewMediaId]));
            var inspected = await Task.WhenAll(request.Replacements.Select(async item =>
            {
                var format = await VgmstreamClient.InspectAsync(
                    converted[item.NewMediaId],
                    vgmstreamPath,
                    cancellationToken);
                return (item.NewMediaId, DurationMs: format.DurationSeconds * 1000);
            }));
            var durationsByNewId = inspected.ToDictionary(item => item.NewMediaId, item => item.DurationMs);
            var durationsByOldId = request.Replacements
                .Where(item => !item.ReferencesAlreadyUseNewId)
                .ToDictionary(item => item.OldMediaId, item => durationsByNewId[item.NewMediaId]);
            var owner = paks.Where(pak => Path.GetFullPath(pak.Path)
                .Equals(Path.GetFullPath(bankAsset.PakPath), StringComparison.OrdinalIgnoreCase)).ToArray();

            if (owner.Length == 0)
            {
                throw new InvalidDataException($"Indexed owner PAK is unavailable: {bankAsset.PakPath}");
            }

            await RepakArchive.ExtractEntryAsync(
                owner,
                bankAsset.EntryPath,
                sourceBank,
                repakPath,
                aesKey,
                cancellationToken);
            await WwiserClient.DumpXmlAsync(
                sourceBank,
                sourceXml,
                wwiserPath,
                pythonPath,
                namesPath,
                cancellationToken);
            var sourceBytes = await File.ReadAllBytesAsync(sourceBank, cancellationToken);
            var authored = BnkTimelineValidator.Validate(
                sourceXml,
                request.ScopeObjectId,
                new Dictionary<uint, double>(),
                request.FromBpm,
                request.FromBpm,
                eventNameOrId: selectedEvent.Id.ToString());
            var authoredScopeIds = BnkRetimer.FindTimingScopes(sourceXml, selectedEvent.Id.ToString())
                .Single(item => item.ObjectId == request.ScopeObjectId)
                .ObjectIds
                .ToHashSet();
            var expectedAtNewBpm = segmentTempos.Length == 0
                ? BnkTimelineValidator.Validate(
                    sourceXml,
                    request.ScopeObjectId,
                    new Dictionary<uint, double>(),
                    request.FromBpm,
                    request.NewBpm,
                    eventNameOrId: selectedEvent.Id.ToString())
                : null;
            var retimePlans = segmentTempos.Length == 0
                ? [BnkRetimer.Plan(
                    sourceBytes,
                    sourceXml,
                    request.ScopeObjectId,
                    request.NewBpm,
                    request.FromBpm,
                    eventNameOrId: selectedEvent.Id.ToString())]
                : segmentTempos.Select(item => BnkRetimer.PlanSegmentOverride(
                    sourceBytes,
                    sourceXml,
                    request.ScopeObjectId,
                    item.SegmentObjectId,
                    item.NewBpm,
                    item.FromBpm,
                    eventNameOrId: selectedEvent.Id.ToString())).ToArray();
            var timed = retimePlans.Aggregate(sourceBytes, BnkRetimer.Apply);
            var retimePatchCount = retimePlans.Sum(plan => plan.Patches.Length);
            var timelineEdits = request.TimelineEdits is { Length: > 0 }
                ? request.TimelineEdits
                : DefaultEdits(
                    authored,
                    request.FromBpm / request.NewBpm,
                    request.Replacements.Where(item => !item.ReferencesAlreadyUseNewId)
                        .Select(item => item.OldMediaId).ToHashSet());

            byte[] editedData;
            int timelinePatchCount;
            var workingXml = sourceXml;
            if (request.PlaylistEdits is { Length: > 0 })
            {
                var fields = BnkTimelineEditor.Apply(timed, authored, timelineEdits, durationsByOldId);
                var markers = BnkTimelineMarkerEditor.Apply(
                    fields.Data, request.MarkerEdits, request.SegmentDurationEdits);
                var structural = BnkTimelineStructureEditor.Apply(markers.Data, sourceXml, request.PlaylistEdits);

                editedData = structural.Data;
                timelinePatchCount = fields.PatchCount + structural.EditedTracks + structural.AddedClips
                    + structural.RemovedClips + structural.MovedAutomations + markers.PatchCount;
                var structuralBank = Path.Combine(temporaryRoot, "structured.bnk");
                workingXml = Path.Combine(temporaryRoot, "structured.xml");
                await File.WriteAllBytesAsync(structuralBank, editedData, cancellationToken);
                await WwiserClient.DumpXmlAsync(
                    structuralBank,
                    workingXml,
                    wwiserPath,
                    pythonPath,
                    namesPath,
                    cancellationToken);
            }
            else
            {
                var edited = BnkTimelineEditor.Apply(timed, authored, timelineEdits, durationsByOldId);
                var markers = BnkTimelineMarkerEditor.Apply(
                    edited.Data, request.MarkerEdits, request.SegmentDurationEdits);

                editedData = markers.Data;
                timelinePatchCount = edited.PatchCount + markers.PatchCount;
            }

            var graph = WwiserHircGraph.Load(workingXml);
            var importRequests = request.Replacements.Select(item =>
            {
                var referencedId = item.ReferencesAlreadyUseNewId ? item.NewMediaId : item.OldMediaId;
                var sourceOffsets = graph.MediaReferenceOffsets(referencedId, authoredScopeIds);
                if (sourceOffsets.Length == 0)
                {
                    throw new InvalidDataException(
                        $"Media {referencedId} has no references inside timing scope {request.ScopeObjectId}.");
                }

                return new BnkMediaImportRequest(
                    item.OldMediaId,
                    item.NewMediaId,
                    replacementBytes[item.NewMediaId],
                    sourceOffsets,
                    graph.MemorySizeOffsets(referencedId, authoredScopeIds),
                    item.ReferencesAlreadyUseNewId,
                    item.ReuseExistingMedia);
            }).ToArray();
            var imported = BnkFile.Parse(editedData).AddMediaAndRedirect(importRequests);

            _ = BnkFile.Parse(imported.Data);

            Directory.CreateDirectory(Path.GetDirectoryName(stagedBank)!);
            await File.WriteAllBytesAsync(stagedBank, imported.Data, cancellationToken);
            var importResults = new List<ScopedMediaImportResult>();
            foreach (var change in imported.Changes)
            {
                string? externalEntry = null;
                if (change.Storage is BnkMediaKind.Unknown or BnkMediaKind.Prefetch)
                {
                    externalEntry = BankSibling(bankAsset.EntryPath, $"{change.NewMediaId}.wem");
                    var stagedWem = StagePath(staging, externalEntry);
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedWem)!);
                    await File.WriteAllBytesAsync(
                        stagedWem,
                        replacementBytes[change.NewMediaId],
                        cancellationToken);
                }

                importResults.Add(new ScopedMediaImportResult(
                    change.OldMediaId,
                    change.NewMediaId,
                    change.Storage,
                    change.ReferenceCount,
                    externalEntry));
            }

            var validationXml = Path.Combine(temporaryRoot, "validation.xml");
            await WwiserClient.DumpXmlAsync(
                stagedBank,
                validationXml,
                wwiserPath,
                pythonPath,
                namesPath,
                cancellationToken);
            ValidateRedirects(validationXml, request, importResults, authoredScopeIds);
            BnkTimelineValidation validation;
            if (segmentTempos.Length == 0)
            {
                validation = BnkTimelineValidator.Validate(
                    validationXml,
                    request.ScopeObjectId,
                    durationsByNewId,
                    request.NewBpm,
                    request.NewBpm,
                    eventNameOrId: selectedEvent.Id.ToString());
                validation = ClassifyExpectedMeterIssues(expectedAtNewBpm!, validation);
            }
            else
            {
                var validations = new List<BnkTimelineValidation>
                {
                    BnkTimelineValidator.Validate(
                        validationXml,
                        request.ScopeObjectId,
                        durationsByNewId,
                        request.FromBpm,
                        request.FromBpm,
                        eventNameOrId: selectedEvent.Id.ToString())
                };
                validations.AddRange(segmentTempos.Select(item => BnkTimelineValidator.Validate(
                    validationXml,
                    item.SegmentObjectId,
                    durationsByNewId,
                    item.NewBpm,
                    item.NewBpm,
                    eventNameOrId: selectedEvent.Id.ToString())));
                validation = MergeIndependentScopes(request.ScopeObjectId, validations, durationsByNewId);
                var expectedValidations = new List<BnkTimelineValidation>
                {
                    BnkTimelineValidator.Validate(
                        validationXml,
                        request.ScopeObjectId,
                        new Dictionary<uint, double>(),
                        request.FromBpm,
                        request.FromBpm,
                        eventNameOrId: selectedEvent.Id.ToString())
                };
                expectedValidations.AddRange(segmentTempos.Select(item => BnkTimelineValidator.Validate(
                    validationXml,
                    item.SegmentObjectId,
                    new Dictionary<uint, double>(),
                    item.NewBpm,
                    item.NewBpm,
                    eventNameOrId: selectedEvent.Id.ToString())));
                var expectedValidation = MergeIndependentScopes(
                    request.ScopeObjectId,
                    expectedValidations,
                    new Dictionary<uint, double>());
                validation = ClassifyExpectedMeterIssues(expectedValidation, validation);
            }
            var errors = validation.Issues.Where(issue => issue.Severity == BnkTimelineSeverity.Error).ToArray();
            if (errors.Length > 0)
            {
                throw new InvalidDataException("Scoped timeline validation failed: "
                    + string.Join("; ", errors.Take(5).Select(issue =>
                        $"{issue.Code} object {issue.ObjectId}"
                        + (issue.MediaId is null ? string.Empty : $" media {issue.MediaId}")
                        + $": {issue.Message}")));
            }

            var expected = new[] { bankAsset.EntryPath }
                .Concat(importResults.Select(item => item.ExternalEntryPath).OfType<string>())
                .Select(NormalizeEntry)
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
                    throw new InvalidDataException("Generated scoped PAK entry verification failed.");
                }

                File.Move(candidate, output, true);
                return new ScopedModPakBuildResult(
                    output,
                    bank.Name,
                    selectedEvent.Id,
                    request.ScopeObjectId,
                    request.FromBpm,
                    request.NewBpm,
                    retimePatchCount + timelinePatchCount,
                    actual,
                    importResults.ToArray(),
                    validation,
                    segmentTempos);
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

    private static BnkTimelineClipEdit[] DefaultEdits(
        BnkTimelineValidation authored,
        double ratio,
        IReadOnlySet<uint> replacedMediaIds) => authored.Clips
        .Where(clip => clip.SourceIdOffset is not null && replacedMediaIds.Contains(clip.MediaId))
        .Select(clip => new BnkTimelineClipEdit(
            clip.SourceIdOffset!.Value,
            Math.Max(0, clip.TimelineStartMs) * ratio,
            Math.Max(0, clip.BeginTrimMs) * ratio,
            Math.Max(1, (clip.TimelineEndMs - Math.Max(0, clip.TimelineStartMs)) * ratio)))
        .ToArray();

    private static BnkTimelineValidation ClassifyExpectedMeterIssues(
        BnkTimelineValidation expected,
        BnkTimelineValidation generated)
    {
        var expectedErrors = expected.Issues
            .Where(issue => issue.Severity == BnkTimelineSeverity.Error)
            .Select(IssueIdentity)
            .ToHashSet();
        var issues = generated.Issues.Select(issue =>
        {
            if (issue.Severity != BnkTimelineSeverity.Error || !expectedErrors.Contains(IssueIdentity(issue)))
            {
                return issue;
            }

            return issue with
            {
                Severity = BnkTimelineSeverity.Warning,
                Code = $"METER_{issue.Code}",
                Message = $"Changing the meter already creates this physical-duration condition. {issue.Message}"
            };
        }).ToArray();

        return generated with { Issues = issues };
    }

    private static BnkTimelineValidation MergeIndependentScopes(
        uint parentScopeObjectId,
        IReadOnlyCollection<BnkTimelineValidation> validations,
        IReadOnlyDictionary<uint, double> replacementDurationsMs)
    {
        var usages = validations.SelectMany(item => item.DurationValidation.ClipUsages)
            .Distinct()
            .ToArray();
        var checks = replacementDurationsMs.OrderBy(item => item.Key).Select(item =>
        {
            var matching = usages.Where(usage => usage.MediaId == item.Key).ToArray();
            var durations = matching.Select(usage => usage.SourceDurationMs).Distinct().Order().ToArray();
            var fit = durations.Length == 0
                ? BnkDurationFit.NotUsed
                : item.Value < durations.Max() - 1
                    ? BnkDurationFit.TooShort
                    : item.Value > durations.Min() + 1
                        ? BnkDurationFit.Longer
                        : BnkDurationFit.Match;
            return new BnkMediaDurationCheck(item.Key, item.Value, durations, matching.Length, fit);
        }).ToArray();
        var issues = validations.SelectMany(item => item.Issues)
            .Where(issue => !issue.Code.StartsWith("MEDIA_", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        issues.AddRange(checks.Where(check => check.Fit != BnkDurationFit.Match).Select(check => new BnkTimelineIssue(
            check.Fit == BnkDurationFit.TooShort ? BnkTimelineSeverity.Error : BnkTimelineSeverity.Warning,
            $"MEDIA_{check.Fit.ToString().ToUpperInvariant()}",
            0,
            check.MediaId,
            $"Replacement duration is {check.Fit}.")));
        return new BnkTimelineValidation(
            parentScopeObjectId,
            1,
            validations.SelectMany(item => item.Segments).DistinctBy(item => item.ObjectId).ToArray(),
            validations.SelectMany(item => item.Clips).Distinct().ToArray(),
            validations.SelectMany(item => item.Transitions).Distinct().ToArray(),
            validations.SelectMany(item => item.Loops).Distinct().ToArray(),
            new BnkDurationValidation(parentScopeObjectId, usages, checks),
            issues.OrderByDescending(item => item.Severity)
                .ThenBy(item => item.Code)
                .ThenBy(item => item.ObjectId)
                .ToArray());
    }

    private static (string Code, uint ObjectId, uint? MediaId) IssueIdentity(BnkTimelineIssue issue) =>
        (issue.Code, issue.ObjectId, issue.MediaId);

    private static void ValidateReplacementIds(WwiseIndex index, IReadOnlyCollection<ScopedMediaReplacement> replacements)
    {
        if (replacements.Where(item => !item.ReferencesAlreadyUseNewId)
                .GroupBy(item => item.OldMediaId).Any(group => group.Count() > 1)
            || replacements.GroupBy(item => item.NewMediaId).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("Scoped replacements contain duplicate old or new media IDs.");
        }

        foreach (var item in replacements)
        {
            if (!WwiseHash.IsMediaId(item.NewMediaId) || item.NewMediaId == item.OldMediaId
                || index.Media.Any(media => media.Id == item.NewMediaId))
            {
                throw new InvalidDataException(
                    $"New media ID {item.NewMediaId} is outside the Hi-Fi RUSH media range, reserved, or already indexed.");
            }
        }
    }

    private static EventRecord ResolveEvent(WwiseIndex index, string value)
    {
        var byId = uint.TryParse(value, out var id);
        var matches = index.Events.Where(item => byId
            ? item.Id == id
            : item.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"No exact Event matching '{value}'."),
            _ => throw new InvalidOperationException($"Event selector '{value}' is ambiguous; use an Event ID.")
        };
    }

    private static void ValidateRedirects(
        string xmlPath,
        ScopedModPakRequest request,
        IReadOnlyCollection<ScopedMediaImportResult> imports,
        IReadOnlySet<uint> objectIds)
    {
        var graph = WwiserHircGraph.Load(xmlPath);
        foreach (var import in imports)
        {
            var requestItem = request.Replacements.Single(item => item.NewMediaId == import.NewMediaId);
            if (!requestItem.ReferencesAlreadyUseNewId
                && graph.MediaReferenceOffsets(import.OldMediaId, objectIds).Length != 0)
            {
                throw new InvalidDataException(
                    $"Old media {import.OldMediaId} still has references inside scope {request.ScopeObjectId}.");
            }

            var redirected = graph.MediaReferenceOffsets(import.NewMediaId, objectIds).Length;
            if (redirected != import.ReferenceCount)
            {
                throw new InvalidDataException(
                    $"New media {import.NewMediaId} has {redirected} scope references; expected {import.ReferenceCount}.");
            }
        }
    }

    private static byte[] ReadCompleteWem(uint id, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Replacement WEM for new media {id} was not found.", fullPath);
        }

        var data = File.ReadAllBytes(fullPath);
        if (data.Length < 12 || !data.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !data.AsSpan(8, 4).SequenceEqual("WAVE"u8)
            || (long)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) + 8 != data.Length)
        {
            throw new InvalidDataException($"Replacement for new media {id} is not a complete RIFF/WAVE WEM.");
        }

        return data;
    }

    private static string BankSibling(string bankEntryPath, string fileName)
    {
        var entry = NormalizeEntry(bankEntryPath);
        var separator = entry.LastIndexOf('/');

        return separator < 0 ? fileName : $"{entry[..separator]}/{fileName}";
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
}
