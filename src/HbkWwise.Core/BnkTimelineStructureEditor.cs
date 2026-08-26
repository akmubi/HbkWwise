using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace HbkWwise.Core;

public sealed record BnkTrackPlaylistItemEdit(
    int? OriginalSourceIdOffset,
    uint MediaId,
    uint SubTrackId,
    uint EventId,
    double StartMs,
    double SourceOffsetMs,
    double DurationMs,
    double SourceDurationMs,
    bool PreserveAutomation = true,
    BnkClipFadeEdit? Fades = null,
    uint? TemplateMediaId = null);

public sealed record BnkClipFadeEdit(double FadeInMs, double FadeOutMs);

public sealed record BnkTrackPlaylistEdit(uint TrackObjectId, BnkTrackPlaylistItemEdit[] Items);

public sealed record BnkTimelineStructureEditResult(
    byte[] Data,
    int EditedTracks,
    int AddedClips,
    int RemovedClips,
    int MovedAutomations);

public static class BnkTimelineStructureEditor
{
    private const int PlaylistItemSize = 44;

    public static BnkTimelineStructureEditResult Apply(
        byte[] bank,
        string wwiserXmlPath,
        IReadOnlyCollection<BnkTrackPlaylistEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentException.ThrowIfNullOrWhiteSpace(wwiserXmlPath);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            return new BnkTimelineStructureEditResult(bank.ToArray(), 0, 0, 0, 0);
        }

        var duplicate = edits.GroupBy(edit => edit.TrackObjectId).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Track {duplicate.Key} has more than one structural edit.");
        }

        var document = XDocument.Load(wwiserXmlPath);
        var tracks = ReadTracks(document, bank);
        var requested = edits.ToDictionary(edit => edit.TrackObjectId);
        var unknown = requested.Keys.Except(tracks.Keys).ToArray();

        if (unknown.Length > 0)
        {
            throw new InvalidDataException($"Music track {unknown[0]} was not found in the bank.");
        }

        var automations = tracks.Values
            .SelectMany(track => track.Playlist.SelectMany(item =>
                track.Automations.GetValueOrDefault(item.Index, []).Select(data =>
                    (item.SourceIdOffset, Data: data))))
            .GroupBy(item => item.SourceIdOffset)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Data).ToArray());
        var sourceTemplatesByMedia = tracks.Values
            .SelectMany(track => track.Sources)
            .DistinctBy(source => source.MediaId)
            .ToDictionary(source => source.MediaId);
        var sourceTemplatesByPlaylistOffset = tracks.Values
            .SelectMany(track => track.Playlist.Select(item =>
                (item.SourceIdOffset, Source: track.Sources.FirstOrDefault(source => source.MediaId == item.MediaId))))
            .Where(item => item.Source is not null)
            .GroupBy(item => item.SourceIdOffset)
            .ToDictionary(group => group.Key, group => group.First().Source!);
        var usedAutomationSources = new HashSet<int>();
        var replacements = new List<ObjectReplacement>();
        var added = 0;
        var removed = 0;
        var movedAutomations = 0;

        foreach (var edit in edits)
        {
            var track = tracks[edit.TrackObjectId];
            ValidateItems(edit, track);
            var replacement = BuildTrackObject(
                bank,
                track,
                edit.Items,
                automations,
                usedAutomationSources,
                sourceTemplatesByMedia,
                sourceTemplatesByPlaylistOffset,
                out var automationCount);
            replacements.Add(new ObjectReplacement(track.ObjectStart, track.ObjectEnd - track.ObjectStart, replacement));
            added += Math.Max(0, edit.Items.Length - track.Playlist.Length);
            removed += Math.Max(0, track.Playlist.Length - edit.Items.Length);
            movedAutomations += automationCount;
        }

        var hirc = FindChunk(bank, "HIRC");
        if (replacements.Any(item => item.Offset < hirc.PayloadOffset || item.Offset + item.Length > hirc.End))
        {
            throw new InvalidDataException("A Music Track edit points outside the HIRC chunk.");
        }

        var delta = replacements.Sum(item => item.Data.Length - item.Length);
        var output = bank.ToArray();

        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(hirc.HeaderOffset + 4, 4),
            checked((uint)(hirc.Size + delta)));
        foreach (var replacement in replacements.OrderByDescending(item => item.Offset))
        {
            output = Replace(output, replacement.Offset, replacement.Length, replacement.Data);
        }

        return new BnkTimelineStructureEditResult(
            output,
            replacements.Count,
            added,
            removed,
            movedAutomations);
    }

    private static byte[] BuildTrackObject(
        byte[] bank,
        TrackLayout track,
        IReadOnlyList<BnkTrackPlaylistItemEdit> items,
        IReadOnlyDictionary<int, byte[][]> automations,
        HashSet<int> usedAutomationSources,
        IReadOnlyDictionary<uint, SourceLayout> sourceTemplatesByMedia,
        IReadOnlyDictionary<int, SourceLayout> sourceTemplatesByPlaylistOffset,
        out int automationCount)
    {
        using var stream = new MemoryStream();
        if (track.SourceCountOffset is { } sourceCountOffset)
        {
            var sources = track.Sources
                .Select(source => (source.MediaId, Data: bank.AsSpan(source.Start, source.End - source.Start).ToArray()))
                .ToList();
            var sourceIds = sources.Select(source => source.MediaId).ToHashSet();

            foreach (var item in items.Where(item => !sourceIds.Contains(item.MediaId)))
            {
                SourceLayout? template = null;
                if (item.TemplateMediaId is { } templateMediaId)
                {
                    sourceTemplatesByMedia.TryGetValue(templateMediaId, out template);
                }

                if (template is null
                    && !sourceTemplatesByMedia.TryGetValue(item.MediaId, out template)
                    && item.OriginalSourceIdOffset is { } originalOffset)
                {
                    sourceTemplatesByPlaylistOffset.TryGetValue(originalOffset, out template);
                }

                if (template is null)
                {
                    throw new InvalidDataException(
                        item.TemplateMediaId is { } missingTemplate
                            ? $"Track source table has no codec template media {missingTemplate} for new media {item.MediaId}."
                            : $"Track source table has no codec template for media {item.MediaId}.");
                }

                var data = bank.AsSpan(template.Start, template.End - template.Start).ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(
                    data.AsSpan(template.SourceIdOffset - template.Start, 4),
                    item.MediaId);
                sources.Add((item.MediaId, data));
                sourceIds.Add(item.MediaId);
            }

            stream.Write(bank.AsSpan(track.ObjectStart, sourceCountOffset - track.ObjectStart));
            WriteUInt32(stream, checked((uint)sources.Count));
            foreach (var source in sources)
            {
                stream.Write(source.Data);
            }
        }
        else
        {
            stream.Write(bank.AsSpan(track.ObjectStart, track.PlaylistCountOffset - track.ObjectStart));
        }

        WriteUInt32(stream, checked((uint)items.Count));
        foreach (var item in items)
        {
            WriteUInt32(stream, item.SubTrackId);
            WriteUInt32(stream, item.MediaId);
            WriteUInt32(stream, item.EventId);
            WriteDouble(stream, item.StartMs - item.SourceOffsetMs);
            WriteDouble(stream, item.SourceOffsetMs);
            WriteDouble(stream, item.DurationMs - (item.SourceDurationMs - item.SourceOffsetMs));
            WriteDouble(stream, item.SourceDurationMs);
        }

        stream.Write(bank.AsSpan(track.PlaylistEnd, track.AutomationCountOffset - track.PlaylistEnd));
        var rewrittenAutomations = new List<byte[]>();
        for (var index = 0; index < items.Count; index++)
        {
            var source = items[index].OriginalSourceIdOffset;
            var sourceAutomations = source is not null && usedAutomationSources.Add(source.Value)
                ? automations.GetValueOrDefault(source.Value, [])
                : [];

            if (items[index].Fades is null)
            {
                if (items[index].PreserveAutomation)
                {
                    rewrittenAutomations.AddRange(sourceAutomations.Select(data => Reindex(data, index)));
                }

                continue;
            }

            if (items[index].PreserveAutomation)
            {
                rewrittenAutomations.AddRange(sourceAutomations
                    .Where(data => AutomationType(data) is not 3 and not 4)
                    .Select(data => Reindex(data, index)));
            }

            var fades = items[index].Fades!;
            if (fades.FadeInMs > 0)
            {
                rewrittenAutomations.Add(FadeIn(index, fades.FadeInMs));
            }

            if (fades.FadeOutMs > 0)
            {
                rewrittenAutomations.Add(FadeOut(index, items[index].DurationMs, fades.FadeOutMs));
            }
        }

        automationCount = rewrittenAutomations.Count;
        WriteUInt32(stream, checked((uint)automationCount));
        foreach (var automation in rewrittenAutomations)
        {
            stream.Write(automation);
        }

        stream.Write(bank.AsSpan(track.AutomationEnd, track.ObjectEnd - track.AutomationEnd));
        var result = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(1, 4), checked((uint)(result.Length - 5)));

        return result;
    }

    private static void ValidateItems(BnkTrackPlaylistEdit edit, TrackLayout track)
    {
        foreach (var item in edit.Items)
        {
            if (item.MediaId == 0 || item.SubTrackId >= track.SubTrackCount
                || !double.IsFinite(item.StartMs) || item.StartMs < 0
                || !double.IsFinite(item.SourceOffsetMs) || item.SourceOffsetMs < 0
                || !double.IsFinite(item.DurationMs) || item.DurationMs <= 0
                || !double.IsFinite(item.SourceDurationMs) || item.SourceDurationMs <= 0
                || item.Fades is { } fades
                && (!double.IsFinite(fades.FadeInMs) || fades.FadeInMs < 0 || fades.FadeInMs > item.DurationMs
                    || !double.IsFinite(fades.FadeOutMs) || fades.FadeOutMs < 0 || fades.FadeOutMs > item.DurationMs))
            {
                throw new InvalidDataException($"Track {edit.TrackObjectId} has an invalid playlist item.");
            }

            if (item.OriginalSourceIdOffset is { } sourceOffset
                && sourceOffset < 0)
            {
                throw new InvalidDataException($"Track {edit.TrackObjectId} has an invalid source template offset.");
            }
        }
    }

    private static uint AutomationType(byte[] data) => data.Length < 8
        ? throw new InvalidDataException("Clip automation record is truncated.")
        : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));

    private static byte[] Reindex(byte[] data, int index)
    {
        var copy = data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy, checked((uint)index));

        return copy;
    }

    private static byte[] FadeIn(int index, double durationMs) => Automation(
        index,
        3,
        [(0, 0, 1), (durationMs / 1000, 1, 9)]);

    private static byte[] FadeOut(int index, double clipDurationMs, double durationMs)
    {
        var end = clipDurationMs / 1000;
        return Automation(index, 4,
            [(0, 1, 9), (Math.Max(0, end - durationMs / 1000), 1, 7), (end, 0, 9)]);
    }

    private static byte[] Automation(
        int index,
        uint type,
        IReadOnlyCollection<(double Time, double Value, uint Interpolation)> points)
    {
        using var stream = new MemoryStream();
        WriteUInt32(stream, checked((uint)index));
        WriteUInt32(stream, type);
        WriteUInt32(stream, checked((uint)points.Count));
        foreach (var point in points)
        {
            WriteFloat(stream, checked((float)point.Time));
            WriteFloat(stream, checked((float)point.Value));
            WriteUInt32(stream, point.Interpolation);
        }

        return stream.ToArray();
    }

    private static Dictionary<uint, TrackLayout> ReadTracks(XDocument document, byte[] bank)
    {
        var result = new Dictionary<uint, TrackLayout>();
        foreach (var node in document.Descendants().Where(item => IsObject(item, "CAkMusicTrack")))
        {
            var objectId = UIntValue(node, "ulID");
            var type = DirectField(node, "eHircType")
                ?? throw new InvalidDataException($"Music track {objectId} has no HIRC type field.");
            var sectionSize = DirectField(node, "dwSectionSize")
                ?? throw new InvalidDataException($"Music track {objectId} has no section-size field.");
            var count = NamedField(node, "numPlaylistItem")
                ?? throw new InvalidDataException($"Music track {objectId} has no playlist count.");
            var sourceCount = NamedField(node, "numSources");
            var subTrackCount = NamedField(node, "numSubTrack")
                ?? throw new InvalidDataException($"Music track {objectId} has no subtrack count.");
            var automationCount = NamedField(node, "numClipAutomationItem")
                ?? throw new InvalidDataException($"Music track {objectId} has no automation count.");
            var objectStart = Offset(type);
            var declaredSize = checked((int)UIntValue(sectionSize));
            var objectEnd = checked(objectStart + 5 + declaredSize);

            if (objectStart < 0 || objectEnd > bank.Length)
            {
                throw new InvalidDataException($"Music track {objectId} exceeds the BNK file.");
            }

            var playlistCountOffset = Offset(count);
            var sources = Array.Empty<SourceLayout>();

            if (sourceCount is not null)
            {
                var sourceNodes = NamedObjects(node, "AkBankSourceData").ToArray();
                var declaredSourceCount = checked((int)UIntValue(sourceCount));

                if (sourceNodes.Length != declaredSourceCount)
                {
                    throw new InvalidDataException($"Music track {objectId} source count does not match wwiser output.");
                }

                var starts = sourceNodes.Select(source => source.DescendantsAndSelf()
                    .Where(IsField)
                    .Where(field => field.Attribute("offset") is not null || field.Attribute("of") is not null)
                    .Select(Offset)
                    .DefaultIfEmpty(-1)
                    .Min()).ToArray();
                if (starts.Any(start => start < 0)
                    || starts.Length > 0 && starts[0] != Offset(sourceCount) + 4)
                {
                    throw new InvalidDataException($"Music track {objectId} has a non-contiguous source table.");
                }

                sources = sourceNodes.Select((source, index) =>
                {
                    var sourceId = NamedField(source, "sourceID")
                        ?? throw new InvalidDataException($"Music track {objectId} source {index} has no media ID.");
                    var end = index + 1 < starts.Length ? starts[index + 1] : playlistCountOffset;
                    var sourceIdOffset = Offset(sourceId);

                    if (starts[index] >= end || sourceIdOffset < starts[index] || sourceIdOffset > end - 4)
                    {
                        throw new InvalidDataException($"Music track {objectId} source {index} has an invalid byte range.");
                    }

                    return new SourceLayout(starts[index], end, sourceIdOffset, UIntValue(sourceId));
                }).ToArray();
            }

            var playlist = NamedObjects(node, "AkTrackSrcInfo").Select((item, index) =>
            {
                var source = DirectField(item, "sourceID")
                    ?? throw new InvalidDataException($"Music track {objectId} playlist item {index} has no source ID.");
                return new PlaylistLayout(index, Offset(source), UIntValue(source));
            }).ToArray();
            var playlistCount = checked((int)UIntValue(count));

            if (playlist.Length != playlistCount)
            {
                throw new InvalidDataException($"Music track {objectId} playlist count does not match wwiser output.");
            }

            var playlistStart = checked(Offset(count) + 4);
            for (var index = 0; index < playlist.Length; index++)
            {
                var expectedSource = checked(playlistStart + index * PlaylistItemSize + 4);
                if (playlist[index].SourceIdOffset != expectedSource)
                {
                    throw new InvalidDataException(
                        $"Music track {objectId} playlist item {index} is not a Wwise 2019.2 AkTrackSrcInfo record.");
                }
            }

            var automationNodes = NamedObjects(node, "AkClipAutomation").ToArray();
            var declaredAutomationCount = checked((int)UIntValue(automationCount));

            if (automationNodes.Length != declaredAutomationCount)
            {
                throw new InvalidDataException($"Music track {objectId} automation count does not match wwiser output.");
            }

            var automationStart = checked(Offset(automationCount) + 4);
            var byIndex = new Dictionary<int, List<byte[]>>();
            var automationEnd = automationStart;

            foreach (var automation in automationNodes)
            {
                var first = DirectField(automation, "uClipIndex")
                    ?? throw new InvalidDataException($"Music track {objectId} has automation without a clip index.");
                var start = Offset(first);
                var end = automation.DescendantsAndSelf()
                    .Where(IsField)
                    .Select(FieldEnd)
                    .DefaultIfEmpty(start)
                    .Max();

                if (start != automationEnd || end > objectEnd)
                {
                    throw new InvalidDataException($"Music track {objectId} has a non-contiguous automation record.");
                }

                var index = checked((int)UIntValue(first));
                if (index < 0 || index >= playlistCount)
                {
                    throw new InvalidDataException($"Music track {objectId} automation refers to playlist item {index}.");
                }

                if (!byIndex.TryGetValue(index, out var records))
                {
                    records = [];
                    byIndex[index] = records;
                }

                records.Add(bank.AsSpan(start, end - start).ToArray());
                automationEnd = end;
            }

            var parsedSubTracks = checked((int)UIntValue(subTrackCount));
            if (parsedSubTracks <= 0)
            {
                throw new InvalidDataException($"Music track {objectId} has no subtracks.");
            }

            result.Add(objectId, new TrackLayout(
                objectStart,
                objectEnd,
                sourceCount is null ? null : Offset(sourceCount),
                sources,
                Offset(count),
                checked(playlistStart + playlistCount * PlaylistItemSize),
                Offset(automationCount),
                automationEnd,
                checked((uint)parsedSubTracks),
                playlist,
                byIndex.ToDictionary(item => item.Key, item => item.Value.ToArray())));
        }

        return result;
    }

    private static Chunk FindChunk(byte[] data, string expectedTag)
    {
        var position = 0;
        while (position + 8 <= data.Length)
        {
            var tag = Encoding.ASCII.GetString(data, position, 4);
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4, 4)));
            var end = checked(position + 8 + size);

            if (end > data.Length)
            {
                throw new InvalidDataException($"BNK chunk {tag} exceeds the file.");
            }

            if (tag == expectedTag)
            {
                return new Chunk(position, position + 8, size, end);
            }

            position = end;
        }

        throw new InvalidDataException($"BNK has no {expectedTag} chunk.");
    }

    private static byte[] Replace(byte[] source, int offset, int length, byte[] replacement)
    {
        var output = new byte[checked(source.Length - length + replacement.Length)];
        source.AsSpan(0, offset).CopyTo(output);
        replacement.CopyTo(output, offset);
        source.AsSpan(offset + length).CopyTo(output.AsSpan(offset + replacement.Length));

        return output;
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteDouble(Stream stream, double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteFloat(Stream stream, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static XElement? DirectField(XElement node, string name) => node.Elements()
        .FirstOrDefault(item => IsField(item) && Name(item) == name);

    private static XElement? NamedField(XElement node, string name) => node.Descendants()
        .FirstOrDefault(item => IsField(item) && Name(item) == name);

    private static IEnumerable<XElement> NamedObjects(XElement node, string name) => node.Descendants()
        .Where(item => IsObject(item, name));

    private static bool IsObject(XElement node, string name) =>
        node.Name.LocalName is "object" or "obj" && Name(node) == name;

    private static bool IsField(XElement node) => node.Name.LocalName is "field" or "fld";

    private static string? Name(XElement node) => node.Attribute("name")?.Value ?? node.Attribute("na")?.Value;

    private static int Offset(XElement node) =>
        ParseInteger(node.Attribute("offset")?.Value ?? node.Attribute("of")?.Value
            ?? throw new InvalidDataException($"{Name(node) ?? node.Name.LocalName} has no file offset."));

    private static uint UIntValue(XElement node, string name) => DirectField(node, name) is { } field
        ? UIntValue(field)
        : 0;

    private static uint UIntValue(XElement field) => uint.Parse(
        field.Attribute("value")?.Value ?? field.Attribute("va")?.Value ?? "0",
        NumberStyles.Integer,
        CultureInfo.InvariantCulture);

    private static int FieldEnd(XElement field)
    {
        var size = (field.Attribute("type")?.Value ?? field.Attribute("ty")?.Value) switch
        {
            "u8" or "s8" or "bit" => 1,
            "u16" or "s16" => 2,
            "u32" or "s32" or "f32" or "tid" or "sid" => 4,
            "u64" or "s64" or "d64" => 8,
            var type => throw new InvalidDataException($"Unsupported wwiser field type '{type}'.")
        };

        return checked(Offset(field) + size);
    }

    private static int ParseInteger(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private sealed record SourceLayout(int Start, int End, int SourceIdOffset, uint MediaId);

    private sealed record PlaylistLayout(int Index, int SourceIdOffset, uint MediaId);

    private sealed record TrackLayout(
        int ObjectStart,
        int ObjectEnd,
        int? SourceCountOffset,
        SourceLayout[] Sources,
        int PlaylistCountOffset,
        int PlaylistEnd,
        int AutomationCountOffset,
        int AutomationEnd,
        uint SubTrackCount,
        PlaylistLayout[] Playlist,
        IReadOnlyDictionary<int, byte[][]> Automations);

    private sealed record ObjectReplacement(int Offset, int Length, byte[] Data);

    private sealed record Chunk(int HeaderOffset, int PayloadOffset, int Size, int End);
}
