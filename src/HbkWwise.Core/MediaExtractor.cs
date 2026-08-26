namespace HbkWwise.Core;

public static class MediaExtractor
{
    public static async Task<MediaRecord> ExtractAsync(
        WwiseIndex index,
        uint mediaId,
        string outputPath,
        string? repakPath = null,
        string? aesKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        var candidates = index.FindMedia(mediaId);
        if (candidates.Count == 0)
        {
            throw new KeyNotFoundException($"Unknown media ID {mediaId}.");
        }

        if (candidates.All(media => media.IsWwiseMidi))
        {
            throw new NotSupportedException(
                $"Media {mediaId} is Wwise MIDI sequence data, not a WEM audio payload, so it cannot be decoded or played directly.");
        }

        var paks = index.Paks is { Length: > 0 }
            ? index.Paks
            : throw new InvalidOperationException("The index was not built from PAK files.");
        Exception? streamedFailure = null;
        foreach (var streamed in candidates.Where(media => media.IsStreamed))
        {
            var asset = streamed.EffectiveAsset();
            var entries = asset is null
                ? RepakArchive.MediaEntryCandidates(streamed)
                : [asset.EntryPath];
            foreach (var entry in entries)
            {
                try
                {
                    if (await TryExtractCompleteAsync(
                        outputPath,
                        path => RepakArchive.ExtractEntryAsync(
                            paks, entry, path, repakPath, aesKey, cancellationToken),
                        cancellationToken))
                    {
                        return streamed;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
                {
                    streamedFailure = exception;
                }
            }
        }

        // Some generated metadata labels a streamed source only as an in-bank prefetch.
        // Probe the conventional sibling WEM before treating that prefix as unplayable.
        foreach (var candidate in candidates)
        {
            var asset = candidate.EffectiveAsset();
            if (asset is null)
            {
                continue;
            }

            var sibling = SiblingEntry(asset.EntryPath, $"{mediaId}.wem");
            try
            {
                if (await TryExtractCompleteAsync(
                    outputPath,
                    path => RepakArchive.ExtractEntryAsync(
                        paks, sibling, path, repakPath, aesKey, cancellationToken),
                    cancellationToken))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
            {
                streamedFailure ??= exception;
            }
        }

        var incompleteBanks = new List<string>();
        foreach (var embedded in candidates.Where(media => media.IsEmbedded))
        {
            var asset = embedded.EffectiveAsset();
            if (asset is null)
            {
                continue;
            }

            var temporaryBank = Path.Combine(Path.GetTempPath(), $"hbkwwise-{Guid.NewGuid():N}.bnk");
            try
            {
                await RepakArchive.ExtractEntryAsync(
                    paks,
                    asset.EntryPath,
                    temporaryBank,
                    repakPath,
                    aesKey,
                    cancellationToken);
                var bank = BnkFile.Read(temporaryBank);
                if (!bank.TryGetMedia(mediaId, out var entry))
                {
                    continue;
                }

                if (entry.Kind != BnkMediaKind.Embedded)
                {
                    incompleteBanks.Add(embedded.Bank);
                    continue;
                }

                await WriteAtomicAsync(outputPath, bank.ExtractCompleteMedia(mediaId), cancellationToken);
                return embedded;
            }
            finally
            {
                File.Delete(temporaryBank);
            }
        }

        throw new InvalidDataException(incompleteBanks.Count == 0
            ? $"No indexed bank contains a complete embedded WEM for media {mediaId}."
            : $"Media {mediaId} has no complete WEM payload; only prefetch or non-audio data was found in {string.Join(", ", incompleteBanks.Distinct(StringComparer.OrdinalIgnoreCase))}.",
            streamedFailure);
    }

    private static async Task<bool> TryExtractCompleteAsync(
        string outputPath,
        Func<string, Task> extract,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"hbkwwise-{Guid.NewGuid():N}.wem");
        try
        {
            await extract(temporary);
            var bytes = await File.ReadAllBytesAsync(temporary, cancellationToken);
            if (!IsCompleteWem(bytes))
            {
                return false;
            }

            await WriteAtomicAsync(outputPath, bytes, cancellationToken);
            return true;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static bool IsCompleteWem(byte[] data)
    {
        if (data.Length < 12
            || !(data.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                || data.AsSpan(0, 4).SequenceEqual("RIFX"u8))
            || !data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var littleEndian = data.AsSpan(0, 4).SequenceEqual("RIFF"u8);
        var declared = littleEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4))
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));

        return declared <= data.Length - 8;
    }

    private static string SiblingEntry(string entryPath, string fileName)
    {
        var normalized = entryPath.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');

        return separator < 0 ? fileName : $"{normalized[..(separator + 1)]}{fileName}";
    }

    private static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = $"{output}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            File.Move(temporary, output, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
