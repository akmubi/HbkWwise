using System.Buffers.Binary;
using System.Text;

namespace HbkWwise.Core;

public enum BnkMediaKind
{
    Unknown,
    Embedded,
    Prefetch
}

public sealed record BnkChunk(string Tag, int Offset, int Size);

public sealed record BnkMediaEntry(uint Id, int Offset, int Size, BnkMediaKind Kind, long? DeclaredSize);

public sealed record BnkMediaChange(uint Id, BnkMediaKind Kind, int OldSize, int StoredSize, int ReplacementSize);

public sealed record BnkRewriteResult(byte[] Data, BnkMediaChange[] Changes);

public sealed record BnkMediaImportResult(
    byte[] Data,
    uint OldMediaId,
    uint NewMediaId,
    BnkMediaKind Storage,
    int StoredSize,
    int ReferenceCount);

public sealed record BnkMediaImportRequest(
    uint OldMediaId,
    uint NewMediaId,
    byte[] Wem,
    IReadOnlyCollection<int> SourceIdOffsets,
    IReadOnlyCollection<int>? MemorySizeOffsets = null,
    bool ReferencesAlreadyUseNewId = false,
    bool ReuseExistingMedia = false);

public sealed record BnkMediaImportChange(
    uint OldMediaId,
    uint NewMediaId,
    BnkMediaKind Storage,
    int StoredSize,
    int ReferenceCount);

public sealed record BnkMediaImportBatchResult(byte[] Data, BnkMediaImportChange[] Changes);

public sealed class BnkFile
{
    private readonly byte[] data;

    private BnkFile(byte[] data, BnkChunk[] chunks, BnkMediaEntry[] media)
    {
        this.data = data;
        Chunks = chunks;
        Media = media;
    }

    public IReadOnlyList<BnkChunk> Chunks { get; }

    public IReadOnlyList<BnkMediaEntry> Media { get; }

    public static BnkFile Read(string path) => Parse(File.ReadAllBytes(path));

    public static BnkFile Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var chunks = ReadChunks(data);
        var didx = chunks.FirstOrDefault(chunk => chunk.Tag == "DIDX")
            ?? throw new InvalidDataException("BNK has no DIDX chunk.");
        var mediaData = chunks.FirstOrDefault(chunk => chunk.Tag == "DATA")
            ?? throw new InvalidDataException("BNK has no DATA chunk.");

        if (didx.Size % 12 != 0)
        {
            throw new InvalidDataException($"DIDX size {didx.Size} is not divisible by 12.");
        }

        var media = new List<BnkMediaEntry>(didx.Size / 12);
        var ids = new HashSet<uint>();

        for (var position = didx.Offset; position < didx.Offset + didx.Size; position += 12)
        {
            var record = data.AsSpan(position, 12);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);

            if (!ids.Add(id))
            {
                throw new InvalidDataException($"DIDX contains duplicate media ID {id}.");
            }

            if ((ulong)relativeOffset + size > (uint)mediaData.Size)
            {
                throw new InvalidDataException($"DIDX media {id} points outside the DATA chunk.");
            }

            var offset = checked(mediaData.Offset + (int)relativeOffset);
            var length = checked((int)size);

            var (kind, declaredSize) = ReadMediaKind(data.AsSpan(offset, length));
            media.Add(new BnkMediaEntry(id, offset, length, kind, declaredSize));
        }

        return new BnkFile(data, chunks, media.ToArray());
    }

    public bool TryGetMedia(uint id, out BnkMediaEntry entry)
    {
        entry = Media.FirstOrDefault(item => item.Id == id)!;
        return entry is not null;
    }

    public byte[] ExtractCompleteMedia(uint id)
    {
        if (!TryGetMedia(id, out var entry))
        {
            throw new KeyNotFoundException($"Media ID {id} is not present in BNK DIDX.");
        }

        if (entry.Kind != BnkMediaKind.Embedded)
        {
            throw new InvalidDataException(entry.Kind == BnkMediaKind.Prefetch
                ? $"Media {id} is only a streaming prefetch, not a complete WEM."
                : $"Media {id} is not a recognized RIFF/WAVE WEM.");
        }

        return data.AsSpan(entry.Offset, entry.Size).ToArray();
    }

    public BnkRewriteResult RewriteMedia(
        IReadOnlyDictionary<uint, byte[]> replacements,
        IReadOnlyDictionary<uint, int[]>? memorySizeOffsets = null)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0)
        {
            return new BnkRewriteResult(data.ToArray(), []);
        }

        foreach (var id in replacements.Keys)
        {
            if (!TryGetMedia(id, out _))
            {
                throw new KeyNotFoundException($"Media ID {id} is not present in BNK DIDX.");
            }
        }

        var patchedBank = data.ToArray();
        var storedMedia = new Dictionary<uint, byte[]>(Media.Count);
        var changes = new List<BnkMediaChange>(replacements.Count);

        foreach (var entry in Media)
        {
            if (!replacements.TryGetValue(entry.Id, out var replacement))
            {
                storedMedia[entry.Id] = data.AsSpan(entry.Offset, entry.Size).ToArray();
                continue;
            }

            if (entry.Kind == BnkMediaKind.Unknown)
            {
                throw new InvalidDataException($"Media {entry.Id} is not a recognized RIFF/WAVE WEM.");
            }

            var (replacementKind, declaredSize) = ReadMediaKind(replacement);
            if (replacementKind != BnkMediaKind.Embedded || declaredSize != replacement.Length)
            {
                throw new InvalidDataException($"Replacement for media {entry.Id} must be a complete RIFF/WAVE WEM with no trailing bytes.");
            }

            ValidateCompatibleFormat(data.AsSpan(entry.Offset, entry.Size), replacement, entry.Id);
            if (entry.Kind == BnkMediaKind.Prefetch && replacement.Length < entry.Size)
            {
                throw new InvalidDataException($"Replacement WEM for media {entry.Id} is {replacement.Length:N0} bytes, smaller than its {entry.Size:N0}-byte prefetch.");
            }

            var stored = entry.Kind == BnkMediaKind.Prefetch ? replacement[..entry.Size] : replacement.ToArray();
            storedMedia[entry.Id] = stored;
            changes.Add(new BnkMediaChange(entry.Id, entry.Kind, entry.Size, stored.Length, replacement.Length));
            if (entry.Kind == BnkMediaKind.Embedded && stored.Length != entry.Size)
            {
                PatchMemorySizes(patchedBank, entry, stored.Length, memorySizeOffsets);
            }
        }

        if (changes.All(change => change.OldSize == change.StoredSize))
        {
            foreach (var change in changes)
            {
                var entry = Media.First(item => item.Id == change.Id);
                storedMedia[change.Id].CopyTo(patchedBank, entry.Offset);
            }

            return new BnkRewriteResult(patchedBank, changes.ToArray());
        }

        var dataChunk = Chunks.Single(chunk => chunk.Tag == "DATA");
        using var mediaData = new MemoryStream();
        var relativeOffsets = new Dictionary<uint, int>(Media.Count);
        for (var index = 0; index < Media.Count; index++)
        {
            var entry = Media[index];
            var originalOffset = entry.Offset - dataChunk.Offset;

            if (index == 0)
            {
                mediaData.Write(patchedBank.AsSpan(dataChunk.Offset, originalOffset));
            }

            relativeOffsets[entry.Id] = checked((int)mediaData.Length);
            mediaData.Write(storedMedia[entry.Id]);
            var originalMediaEnd = checked(originalOffset + entry.Size);
            if (index + 1 < Media.Count)
            {
                var nextOffset = Media[index + 1].Offset - dataChunk.Offset;
                var originalPadding = nextOffset - originalMediaEnd;
                var minimumPadding = Align16(originalMediaEnd) - originalMediaEnd;

                if (originalPadding < minimumPadding)
                {
                    throw new InvalidDataException("DIDX media entries overlap or are not ordered by DATA offset.");
                }

                var newPadding = Align16(checked((int)mediaData.Length)) - (int)mediaData.Length
                    + originalPadding - minimumPadding;
                mediaData.SetLength(mediaData.Length + newPadding);
                mediaData.Position = mediaData.Length;
            }
            else
            {
                mediaData.Write(patchedBank.AsSpan(dataChunk.Offset + originalMediaEnd, dataChunk.Size - originalMediaEnd));
            }
        }

        var didx = new byte[checked(Media.Count * 12)];
        for (var index = 0; index < Media.Count; index++)
        {
            var entry = Media[index];
            var record = didx.AsSpan(index * 12, 12);

            BinaryPrimitives.WriteUInt32LittleEndian(record, entry.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], checked((uint)relativeOffsets[entry.Id]));
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], checked((uint)storedMedia[entry.Id].Length));
        }

        using var rewritten = new MemoryStream();
        foreach (var chunk in Chunks)
        {
            var payload = chunk.Tag switch
            {
                "DIDX" => didx,
                "DATA" => mediaData.ToArray(),
                _ => patchedBank.AsSpan(chunk.Offset, chunk.Size).ToArray()
            };
            WriteChunk(rewritten, chunk.Tag, payload);
        }

        var originalEnd = Chunks.Count == 0 ? 0 : Chunks[^1].Offset + Chunks[^1].Size;
        rewritten.Write(patchedBank.AsSpan(originalEnd));

        return new BnkRewriteResult(rewritten.ToArray(), changes.ToArray());
    }

    public BnkMediaImportResult AddMediaAndRedirect(
        uint oldMediaId,
        uint newMediaId,
        byte[] newWem,
        IReadOnlyCollection<int> sourceIdOffsets,
        IReadOnlyCollection<int>? memorySizeOffsets = null)
    {
        var result = AddMediaAndRedirect([
            new BnkMediaImportRequest(oldMediaId, newMediaId, newWem, sourceIdOffsets, memorySizeOffsets)
        ]);
        var change = AssertSingle(result.Changes);

        return new BnkMediaImportResult(
            result.Data,
            change.OldMediaId,
            change.NewMediaId,
            change.Storage,
            change.StoredSize,
            change.ReferenceCount);
    }

    public BnkMediaImportBatchResult AddMediaAndRedirect(IReadOnlyCollection<BnkMediaImportRequest> imports)
    {
        ArgumentNullException.ThrowIfNull(imports);
        if (imports.Count == 0)
        {
            return new BnkMediaImportBatchResult(data.ToArray(), []);
        }

        var requests = imports.ToArray();
        if (requests.Where(item => !item.ReferencesAlreadyUseNewId)
            .GroupBy(item => item.OldMediaId).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("A batch cannot redirect the same old media ID more than once.");
        }

        if (requests.GroupBy(item => item.NewMediaId).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("A batch contains duplicate new media IDs.");
        }

        var patched = data.ToArray();
        var additions = new List<(uint Id, byte[] Data)>();
        var changes = new List<BnkMediaImportChange>(requests.Length);
        var claimedOffsets = new HashSet<int>();

        foreach (var request in requests)
        {
            ArgumentNullException.ThrowIfNull(request.Wem);
            ArgumentNullException.ThrowIfNull(request.SourceIdOffsets);
            if (request.OldMediaId == request.NewMediaId)
            {
                throw new ArgumentException("The new media ID must differ from the old media ID.", nameof(imports));
            }

            var existingNewMedia = Media.FirstOrDefault(item => item.Id == request.NewMediaId);
            if (!WwiseHash.IsMediaId(request.NewMediaId)
                || existingNewMedia is not null && !request.ReuseExistingMedia)
            {
                throw new InvalidDataException(
                    $"Media ID {request.NewMediaId} is outside the Hi-Fi RUSH media range, already exists, or is reserved.");
            }

            var offsets = request.SourceIdOffsets.Distinct().Order().ToArray();
            if (offsets.Length == 0)
            {
                throw new InvalidDataException(
                    $"No HIRC sourceID references to media {(request.ReferencesAlreadyUseNewId ? request.NewMediaId : request.OldMediaId)} were selected.");
            }

            var (newKind, declaredSize) = ReadMediaKind(request.Wem);
            if (newKind != BnkMediaKind.Embedded || declaredSize != request.Wem.Length)
            {
                throw new InvalidDataException(
                    $"New media {request.NewMediaId} must be a complete RIFF/WAVE WEM with no trailing bytes.");
            }

            foreach (var offset in offsets)
            {
                if (!claimedOffsets.Add(offset) || offset < 0 || offset > patched.Length - 4)
                {
                    throw new InvalidDataException($"Invalid or duplicate HIRC sourceID offset 0x{offset:X}.");
                }

                var current = BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(offset, 4));
                var expected = request.ReferencesAlreadyUseNewId ? request.NewMediaId : request.OldMediaId;

                if (current != expected)
                {
                    throw new InvalidDataException(
                        $"HIRC sourceID at 0x{offset:X} is {current}, expected {expected}.");
                }

                if (!request.ReferencesAlreadyUseNewId)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(offset, 4), request.NewMediaId);
                }
            }

            var storage = BnkMediaKind.Unknown;
            var storedSize = 0;

            if (existingNewMedia is not null)
            {
                var stored = data.AsSpan(existingNewMedia.Offset, existingNewMedia.Size);
                if (existingNewMedia.Kind == BnkMediaKind.Unknown
                    || request.Wem.Length < stored.Length
                    || !request.Wem.AsSpan(0, stored.Length).SequenceEqual(stored))
                {
                    throw new InvalidDataException(
                        $"Existing media {request.NewMediaId} does not match the reused WEM payload.");
                }

                storage = existingNewMedia.Kind;
                storedSize = existingNewMedia.Size;
            }

            if (existingNewMedia is null && TryGetMedia(request.OldMediaId, out var original))
            {
                if (original.Kind == BnkMediaKind.Unknown)
                {
                    throw new InvalidDataException($"Media {request.OldMediaId} is not a recognized RIFF/WAVE WEM.");
                }

                ValidateCompatibleFormat(data.AsSpan(original.Offset, original.Size), request.Wem, request.OldMediaId);
                if (original.Kind == BnkMediaKind.Prefetch && request.Wem.Length < original.Size)
                {
                    throw new InvalidDataException(
                        $"New WEM {request.NewMediaId} is smaller than the required {original.Size:N0}-byte prefetch.");
                }

                var stored = original.Kind == BnkMediaKind.Prefetch ? request.Wem[..original.Size] : request.Wem;
                if (original.Kind == BnkMediaKind.Embedded && stored.Length != original.Size)
                {
                    PatchMemorySizes(
                        patched,
                        original,
                        stored.Length,
                        new Dictionary<uint, int[]>
                        {
                            [request.OldMediaId] = request.MemorySizeOffsets?.Distinct().ToArray() ?? []
                        });
                }

                storage = original.Kind;
                storedSize = stored.Length;
                additions.Add((request.NewMediaId, stored));
            }

            changes.Add(new BnkMediaImportChange(
                request.OldMediaId,
                request.NewMediaId,
                storage,
                storedSize,
                offsets.Length));
        }

        if (additions.Count == 0)
        {
            return new BnkMediaImportBatchResult(patched, changes.ToArray());
        }

        var didx = Chunks.Single(chunk => chunk.Tag == "DIDX");
        var mediaData = Chunks.Single(chunk => chunk.Tag == "DATA");
        var newDidx = new byte[checked(didx.Size + additions.Count * 12)];

        patched.AsSpan(didx.Offset, didx.Size).CopyTo(newDidx);
        using var newData = new MemoryStream();
        newData.Write(patched.AsSpan(mediaData.Offset, mediaData.Size));
        for (var index = 0; index < additions.Count; index++)
        {
            newData.SetLength(Align16(checked((int)newData.Length)));
            newData.Position = newData.Length;
            var addition = additions[index];
            var record = newDidx.AsSpan(didx.Size + index * 12, 12);

            BinaryPrimitives.WriteUInt32LittleEndian(record, addition.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], checked((uint)newData.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], checked((uint)addition.Data.Length));
            newData.Write(addition.Data);
        }

        using var rewritten = new MemoryStream();
        foreach (var chunk in Chunks)
        {
            var payload = chunk.Tag switch
            {
                "DIDX" => newDidx,
                "DATA" => newData.ToArray(),
                _ => patched.AsSpan(chunk.Offset, chunk.Size).ToArray()
            };
            WriteChunk(rewritten, chunk.Tag, payload);
        }

        var originalEnd = Chunks[^1].Offset + Chunks[^1].Size;
        rewritten.Write(patched.AsSpan(originalEnd));

        return new BnkMediaImportBatchResult(rewritten.ToArray(), changes.ToArray());
    }

    private static T AssertSingle<T>(IReadOnlyCollection<T> items) => items.Count == 1
        ? items.First()
        : throw new InvalidOperationException($"Expected one item, found {items.Count}.");

    private static BnkChunk[] ReadChunks(byte[] data)
    {
        var chunks = new List<BnkChunk>();
        var position = 0;

        while (position + 8 <= data.Length)
        {
            var header = data.AsSpan(position, 8);
            var tag = Encoding.ASCII.GetString(header[..4]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var offset = position + 8;

            if ((ulong)offset + size > (uint)data.Length)
            {
                throw new InvalidDataException($"BNK chunk {tag} at 0x{position:X} exceeds the file.");
            }

            chunks.Add(new BnkChunk(tag, offset, checked((int)size)));
            position = checked(offset + (int)size);
        }

        return chunks.ToArray();
    }

    private static (BnkMediaKind Kind, long? DeclaredSize) ReadMediaKind(ReadOnlySpan<byte> media)
    {
        if (media.Length < 12 || !media[..4].SequenceEqual("RIFF"u8) || !media[8..12].SequenceEqual("WAVE"u8))
        {
            return (BnkMediaKind.Unknown, null);
        }

        var declaredSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(media[4..8]) + 8;
        return (declaredSize <= media.Length ? BnkMediaKind.Embedded : BnkMediaKind.Prefetch, declaredSize);
    }

    private static void PatchMemorySizes(
        byte[] bank,
        BnkMediaEntry entry,
        int newSize,
        IReadOnlyDictionary<uint, int[]>? offsetsByMedia)
    {
        if (offsetsByMedia is null || !offsetsByMedia.TryGetValue(entry.Id, out var offsets) || offsets.Length == 0)
        {
            throw new InvalidDataException($"Size-changing embedded media {entry.Id} requires uInMemoryMediaSize offsets from wwiser XML.");
        }

        foreach (var offset in offsets.Distinct())
        {
            if (offset < 0 || offset > bank.Length - 4)
            {
                throw new InvalidDataException($"wwiser reported an invalid memory-size offset 0x{offset:X} for media {entry.Id}.");
            }

            var oldSize = BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(offset, 4));
            if (oldSize != entry.Size)
            {
                throw new InvalidDataException($"Memory-size field at 0x{offset:X} is {oldSize}, expected {entry.Size} for media {entry.Id}.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(offset, 4), checked((uint)newSize));
        }
    }

    private static void ValidateCompatibleFormat(ReadOnlySpan<byte> original, ReadOnlySpan<byte> replacement, uint id)
    {
        var oldFormat = ReadWaveFormat(original);
        var newFormat = ReadWaveFormat(replacement);

        if (oldFormat != newFormat)
        {
            throw new InvalidDataException(
                $"Media {id} format mismatch: expected tag 0x{oldFormat.Tag:X4}, {oldFormat.Channels} channel(s), {oldFormat.SampleRate} Hz; "
                + $"replacement is tag 0x{newFormat.Tag:X4}, {newFormat.Channels} channel(s), {newFormat.SampleRate} Hz.");
        }
    }

    private static WemFormat ReadWaveFormat(ReadOnlySpan<byte> media)
    {
        var position = 12;
        while (position <= media.Length - 8)
        {
            var header = media[position..];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var payload = position + 8;

            if ((ulong)payload + size > (uint)media.Length)
            {
                break;
            }

            if (header[..4].SequenceEqual("fmt "u8) && size >= 16)
            {
                var format = media[payload..];
                return new WemFormat(
                    BinaryPrimitives.ReadUInt16LittleEndian(format),
                    BinaryPrimitives.ReadUInt16LittleEndian(format[2..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(format[4..]));
            }

            position = checked(payload + (int)size + ((int)size & 1));
        }

        throw new InvalidDataException("WEM has no complete WAVE fmt chunk.");
    }

    private static void WriteChunk(Stream output, string tag, byte[] payload)
    {
        output.Write(Encoding.ASCII.GetBytes(tag));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)payload.Length));
        output.Write(size);
        output.Write(payload);
    }

    private static int Align16(int value) => checked((value + 15) & ~15);

    private readonly record struct WemFormat(ushort Tag, ushort Channels, uint SampleRate);
}
