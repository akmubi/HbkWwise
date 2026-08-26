using System.Buffers.Binary;
using System.Text;
using HbkWwise.Core;

namespace HbkWwise.Core.Tests;

public sealed class BnkFileTests
{
    [Fact]
    public void Parse_ExtractsCompleteDidxMedia()
    {
        var wem = Wem(16);
        var bank = BnkFile.Parse(Bank(428446315, wem));

        var entry = Assert.Single(bank.Media);

        Assert.Equal(BnkMediaKind.Embedded, entry.Kind);
        Assert.Equal(16, entry.DeclaredSize);
        Assert.Equal(wem, bank.ExtractCompleteMedia(428446315));
    }

    [Fact]
    public void Parse_RejectsPrefetchAsCompleteMedia()
    {
        var bank = BnkFile.Parse(Bank(1, Wem(100)));

        Assert.Equal(BnkMediaKind.Prefetch, Assert.Single(bank.Media).Kind);
        Assert.Throws<InvalidDataException>(() => bank.ExtractCompleteMedia(1));
    }

    [Fact]
    public void Parse_RejectsDidxRangeOutsideData()
    {
        var bytes = Bank(1, Wem(16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 100);

        Assert.Throws<InvalidDataException>(() => BnkFile.Parse(bytes));
    }

    [Fact]
    public void RewriteMedia_ResizesEmbeddedMediaAndPatchesMemoryField()
    {
        var original = CompleteWem(21);
        var untouched = CompleteWem(12);
        var replacement = CompleteWem(54);
        var baseBank = Bank([(1, original), (2, untouched)]);
        var memoryPayload = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(memoryPayload, (uint)original.Length);
        var memoryOffset = baseBank.Length + 8;
        var bank = BnkFile.Parse([.. baseBank, .. Chunk("HIRC", memoryPayload)]);

        var result = bank.RewriteMedia(
            new Dictionary<uint, byte[]> { [1] = replacement },
            new Dictionary<uint, int[]> { [1] = [memoryOffset] });

        var rewritten = BnkFile.Parse(result.Data);

        Assert.Equal(replacement, rewritten.ExtractCompleteMedia(1));
        Assert.Equal(untouched, rewritten.ExtractCompleteMedia(2));

        var hirc = Assert.Single(rewritten.Chunks, chunk => chunk.Tag == "HIRC");

        Assert.Equal((uint)replacement.Length, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset, 4)));

        var change = Assert.Single(result.Changes);

        Assert.Equal(original.Length, change.OldSize);
        Assert.Equal(replacement.Length, change.StoredSize);
    }

    [Fact]
    public void RewriteMedia_ReplacesOnlyFixedSizeStreamingPrefetch()
    {
        var original = CompleteWem(100);
        var replacement = CompleteWem(120, fill: 0x5A);
        var prefetch = original[..60];
        var bank = BnkFile.Parse(Bank(1, prefetch));

        var result = bank.RewriteMedia(new Dictionary<uint, byte[]> { [1] = replacement });

        var rewritten = BnkFile.Parse(result.Data);
        var entry = Assert.Single(rewritten.Media);

        Assert.Equal(BnkMediaKind.Prefetch, entry.Kind);
        Assert.Equal(prefetch.Length, entry.Size);
        Assert.Equal(replacement[..prefetch.Length], result.Data.AsSpan(entry.Offset, entry.Size).ToArray());
        Assert.Equal(replacement.Length, Assert.Single(result.Changes).ReplacementSize);
    }

    [Fact]
    public void RewriteMedia_RequiresOffsetsWhenEmbeddedSizeChanges()
    {
        var bank = BnkFile.Parse(Bank(1, CompleteWem(20)));

        var error = Assert.Throws<InvalidDataException>(() =>
            bank.RewriteMedia(new Dictionary<uint, byte[]> { [1] = CompleteWem(30) }));

        Assert.Contains("uInMemoryMediaSize", error.Message);
    }

    [Fact]
    public void RewriteMedia_RejectsIncompatibleFormat()
    {
        var bank = BnkFile.Parse(Bank(1, CompleteWem(20, channels: 2)));

        Assert.Throws<InvalidDataException>(() =>
            bank.RewriteMedia(new Dictionary<uint, byte[]> { [1] = CompleteWem(20, channels: 1) }));
    }

    [Fact]
    public void AddMediaAndRedirect_KeepsOldEmbeddedMediaAndAddsNewEntry()
    {
        var original = CompleteWem(20);
        var imported = CompleteWem(44, fill: 0x5A);
        var baseBank = Bank(10, original);
        var hircPayload = new byte[8];

        BinaryPrimitives.WriteUInt32LittleEndian(hircPayload, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(hircPayload.AsSpan(4), (uint)original.Length);
        var referenceOffset = baseBank.Length + 8;
        var memoryOffset = referenceOffset + 4;
        var bank = BnkFile.Parse([.. baseBank, .. Chunk("HIRC", hircPayload)]);

        var result = bank.AddMediaAndRedirect(10, 20, imported, [referenceOffset], [memoryOffset]);

        var rewritten = BnkFile.Parse(result.Data);

        Assert.Equal(original, rewritten.ExtractCompleteMedia(10));
        Assert.Equal(imported, rewritten.ExtractCompleteMedia(20));

        var hirc = Assert.Single(rewritten.Chunks, chunk => chunk.Tag == "HIRC");

        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset, 4)));
        Assert.Equal((uint)imported.Length, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset + 4, 4)));
        Assert.Equal(1, result.ReferenceCount);
    }

    [Fact]
    public void AddMediaAndRedirect_StreamedMediaOnlyPatchesReference()
    {
        var baseBank = Bank(1, CompleteWem(10));
        var referencePayload = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(referencePayload, 77);
        var referenceOffset = baseBank.Length + 8;
        var bank = BnkFile.Parse([.. baseBank, .. Chunk("HIRC", referencePayload)]);

        var result = bank.AddMediaAndRedirect(77, 88, CompleteWem(20), [referenceOffset]);

        var rewritten = BnkFile.Parse(result.Data);

        Assert.Single(rewritten.Media);

        var hirc = Assert.Single(rewritten.Chunks, chunk => chunk.Tag == "HIRC");

        Assert.Equal(88u, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset, 4)));
        Assert.Equal(BnkMediaKind.Unknown, result.Storage);
    }

    [Fact]
    public void AddMediaAndRedirect_RejectsMediaIdOutsideGameRange()
    {
        var baseBank = Bank(1, CompleteWem(10));
        var referencePayload = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(referencePayload, 77);
        var referenceOffset = baseBank.Length + 8;
        var bank = BnkFile.Parse([.. baseBank, .. Chunk("HIRC", referencePayload)]);

        var exception = Assert.Throws<InvalidDataException>(() => bank.AddMediaAndRedirect(
            77,
            WwiseHash.MaxMediaId + 1,
            CompleteWem(20),
            [referenceOffset]));

        Assert.Contains("outside the Hi-Fi RUSH media range", exception.Message);
    }

    [Fact]
    public void AddMediaAndRedirect_BatchPatchesOriginalHircOffsetsAndAppendsAllMedia()
    {
        var originalA = CompleteWem(20);
        var originalB = CompleteWem(24);
        var importedA = CompleteWem(40, fill: 0x11);
        var importedB = CompleteWem(48, fill: 0x22);
        var baseBank = Bank([(10, originalA), (11, originalB)]);
        var hircPayload = new byte[16];

        BinaryPrimitives.WriteUInt32LittleEndian(hircPayload, 10);
        BinaryPrimitives.WriteUInt32LittleEndian(hircPayload.AsSpan(4), (uint)originalA.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(hircPayload.AsSpan(8), 11);
        BinaryPrimitives.WriteUInt32LittleEndian(hircPayload.AsSpan(12), (uint)originalB.Length);
        var hircOffset = baseBank.Length + 8;
        var bank = BnkFile.Parse([.. baseBank, .. Chunk("HIRC", hircPayload)]);

        var result = bank.AddMediaAndRedirect([
            new BnkMediaImportRequest(10, 20, importedA, [hircOffset], [hircOffset + 4]),
            new BnkMediaImportRequest(11, 21, importedB, [hircOffset + 8], [hircOffset + 12])
        ]);

        var rewritten = BnkFile.Parse(result.Data);

        Assert.Equal(4, rewritten.Media.Count);
        Assert.Equal(importedA, rewritten.ExtractCompleteMedia(20));
        Assert.Equal(importedB, rewritten.ExtractCompleteMedia(21));

        var hirc = Assert.Single(rewritten.Chunks, chunk => chunk.Tag == "HIRC");

        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset, 4)));
        Assert.Equal((uint)importedA.Length, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset + 4, 4)));
        Assert.Equal(21u, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset + 8, 4)));
        Assert.Equal((uint)importedB.Length, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset + 12, 4)));
        Assert.Equal(2, result.Changes.Length);
    }

    [Fact]
    public void AddMediaAndRedirect_AddsMediaWhenStructuralReferenceAlreadyUsesNewId()
    {
        var original = CompleteWem(20);
        var imported = CompleteWem(40, fill: 0x33);
        var baseBank = Bank(10, original);
        var hircPayload = new byte[8];

        BinaryPrimitives.WriteUInt32LittleEndian(hircPayload, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(hircPayload.AsSpan(4), (uint)original.Length);
        var referenceOffset = baseBank.Length + 8;
        var bank = BnkFile.Parse([.. baseBank, .. Chunk("HIRC", hircPayload)]);

        var result = bank.AddMediaAndRedirect([
            new BnkMediaImportRequest(
                10,
                20,
                imported,
                [referenceOffset],
                [referenceOffset + 4],
                ReferencesAlreadyUseNewId: true)
        ]);

        var rewritten = BnkFile.Parse(result.Data);

        Assert.Equal(imported, rewritten.ExtractCompleteMedia(20));

        var hirc = Assert.Single(rewritten.Chunks, chunk => chunk.Tag == "HIRC");

        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset, 4)));
        Assert.Equal((uint)imported.Length, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset + 4, 4)));
        Assert.Equal(1, Assert.Single(result.Changes).ReferenceCount);
    }

    [Fact]
    public void AddMediaAndRedirect_ReusesMediaImportedByAnEarlierScope()
    {
        var original = CompleteWem(20);
        var imported = CompleteWem(40, fill: 0x44);
        var baseBank = Bank([(10, original), (20, imported)]);
        var hircPayload = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(hircPayload, 10);
        var referenceOffset = baseBank.Length + 8;
        var bank = BnkFile.Parse([.. baseBank, .. Chunk("HIRC", hircPayload)]);

        var result = bank.AddMediaAndRedirect([
            new BnkMediaImportRequest(
                10,
                20,
                imported,
                [referenceOffset],
                ReuseExistingMedia: true)
        ]);
        var rewritten = BnkFile.Parse(result.Data);
        var hirc = Assert.Single(rewritten.Chunks, chunk => chunk.Tag == "HIRC");

        Assert.Equal(2, rewritten.Media.Count);
        Assert.Equal(imported, rewritten.ExtractCompleteMedia(20));
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(result.Data.AsSpan(hirc.Offset, 4)));
    }

    private static byte[] Bank(uint id, byte[] wem)
        => Bank([(id, wem)]);

    private static byte[] Bank((uint Id, byte[] Wem)[] media)
    {
        var didx = new byte[media.Length * 12];
        using var data = new MemoryStream();
        for (var index = 0; index < media.Length; index++)
        {
            data.SetLength((data.Length + 15) & ~15);
            data.Position = data.Length;
            var record = didx.AsSpan(index * 12, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(record, media[index].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], (uint)data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)media[index].Wem.Length);
            data.Write(media[index].Wem);
        }

        return [.. Chunk("DIDX", didx), .. Chunk("DATA", data.ToArray())];
    }

    private static byte[] Wem(int declaredSize)
    {
        var bytes = new byte[16];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)declaredSize - 8);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));

        return bytes;
    }

    private static byte[] CompleteWem(int payloadSize, ushort channels = 2, byte fill = 0)
    {
        var bytes = new byte[44 + payloadSize];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), channels);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 48_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 192_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)payloadSize);
        bytes.AsSpan(44).Fill(fill);

        return bytes;
    }

    private static byte[] Chunk(string tag, byte[] payload)
    {
        var bytes = new byte[payload.Length + 8];
        Encoding.ASCII.GetBytes(tag).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);

        return bytes;
    }
}
