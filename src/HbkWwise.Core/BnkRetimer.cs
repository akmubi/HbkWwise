using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;

namespace HbkWwise.Core;

public sealed record BnkRetimePatch(
    string Kind,
    int Offset,
    uint ObjectId,
    uint? MediaId,
    double OldValue,
    double NewValue,
    byte[] OldBytes,
    byte[] NewBytes);

public sealed record BnkRetimePlan(
    uint ScopeObjectId,
    double FromBpm,
    double NewBpm,
    double Ratio,
    uint[] AffectedMediaIds,
    uint[] RetimeObjectIds,
    BnkRetimePatch[] Patches)
{
    public int TimelinePatchCount => Patches.Count(patch => !patch.Kind.StartsWith("meter-", StringComparison.Ordinal));
}

public sealed record BnkTimingScope(
    uint ObjectId,
    int ObjectType,
    string ObjectClass,
    double[] Bpms,
    uint[] ObjectIds,
    uint[] MediaIds)
{
    public int RetimeObjects => ObjectIds.Length;
}

public static class BnkRetimer
{
    public static BnkRetimePlan Plan(
        byte[] bank,
        string wwiserXmlPath,
        uint scopeObjectId,
        double newBpm,
        double fromBpm,
        bool retimeTimeline = true,
        double epsilon = 0.01,
        string? eventNameOrId = null)
    {
        ArgumentNullException.ThrowIfNull(bank);
        if (newBpm <= 0 || fromBpm <= 0 || epsilon < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newBpm), "BPM values must be positive and epsilon must be non-negative.");
        }

        var document = XDocument.Load(wwiserXmlPath);
        var graph = WwiserHircGraph.Load(wwiserXmlPath);
        var objects = ReadObjects(document);
        var allowed = eventNameOrId is null
            ? null
            : graph.WithAncestors(graph.PlayScope(eventNameOrId).ReachableObjectIds);

        if (allowed is not null && !allowed.Contains(scopeObjectId))
        {
            throw new InvalidDataException($"Timing scope {scopeObjectId} is not reachable from Event '{eventNameOrId}'.");
        }

        if (!objects.TryGetValue(scopeObjectId, out var scopeObject) || !graph.Objects.ContainsKey(scopeObjectId))
        {
            throw new InvalidDataException($"Timing scope HIRC object {scopeObjectId} was not found in this bank.");
        }

        var selectedMeters = ActiveMeters(scopeObject)
            .Where(item => Math.Abs(item.Tempo - fromBpm) <= epsilon)
            .ToArray();
        if (selectedMeters.Length != 1)
        {
            throw new InvalidDataException(selectedMeters.Length == 0
                ? $"Timing scope {scopeObjectId} has no active meter matching {fromBpm:g} BPM."
                : $"Timing scope {scopeObjectId} has multiple active meters matching {fromBpm:g} BPM.");
        }

        var meter = selectedMeters[0];
        var ratio = (double)meter.Tempo / newBpm;
        var objectIds = ScopeObjectIds(graph, objects, scopeObjectId, null);

        var patches = new Dictionary<int, BnkRetimePatch>();
        AddDouble(patches, bank, "meter-grid", meter.GridField, scopeObjectId, null, meter.Grid, meter.Grid * ratio);
        AddDouble(patches, bank, "meter-offset", meter.OffsetField, scopeObjectId, null, meter.GridOffset, meter.GridOffset * ratio);
        AddFloat(patches, bank, "meter-tempo", meter.TempoField, scopeObjectId, meter.Tempo, checked((float)newBpm));

        if (retimeTimeline)
        {
            foreach (var id in objectIds)
            {
                AddTimelinePatches(patches, bank, objects[id], id, ratio);
            }
        }

        return new BnkRetimePlan(
            scopeObjectId,
            fromBpm,
            newBpm,
            ratio,
            objectIds
                .Where(graph.Objects.ContainsKey)
                .SelectMany(id => graph.Objects[id].Media)
                .Select(media => media.MediaId)
                .Distinct()
                .Order()
                .ToArray(),
            objectIds,
            patches.Values.OrderBy(patch => patch.Offset).ToArray());
    }

    public static BnkRetimePlan PlanSegmentOverride(
        byte[] bank,
        string wwiserXmlPath,
        uint parentScopeObjectId,
        uint segmentObjectId,
        double newBpm,
        double fromBpm,
        double epsilon = 0.01,
        string? eventNameOrId = null)
    {
        ArgumentNullException.ThrowIfNull(bank);
        if (newBpm <= 0 || fromBpm <= 0 || epsilon < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newBpm));
        }

        var document = XDocument.Load(wwiserXmlPath);
        var graph = WwiserHircGraph.Load(wwiserXmlPath);
        var objects = ReadObjects(document);
        var parent = objects.GetValueOrDefault(parentScopeObjectId)
            ?? throw new InvalidDataException($"Parent timing scope {parentScopeObjectId} was not found.");
        var segment = objects.GetValueOrDefault(segmentObjectId)
            ?? throw new InvalidDataException($"Music segment {segmentObjectId} was not found.");

        if (!graph.Objects.TryGetValue(segmentObjectId, out var graphSegment) || graphSegment.Type != 10)
        {
            throw new InvalidDataException($"HIRC object {segmentObjectId} is not a Music Segment.");
        }

        var allowed = eventNameOrId is null
            ? graph.Objects.Keys.ToHashSet()
            : graph.WithAncestors(graph.PlayScope(eventNameOrId).ReachableObjectIds);
        var parentObjects = ScopeObjectIds(graph, objects, parentScopeObjectId, allowed);

        if (!parentObjects.Contains(segmentObjectId))
        {
            throw new InvalidDataException(
                $"Music segment {segmentObjectId} is outside timing scope {parentScopeObjectId}.");
        }

        var inherited = ActiveMeters(parent)
            .Where(item => Math.Abs(item.Tempo - fromBpm) <= epsilon)
            .ToArray();
        if (inherited.Length != 1)
        {
            throw new InvalidDataException(
                $"Parent timing scope {parentScopeObjectId} does not have exactly one {fromBpm:g} BPM meter.");
        }

        var localMeters = Meters(segment).ToArray();
        if (localMeters.Length != 1)
        {
            throw new InvalidDataException(
                $"Music segment {segmentObjectId} does not have exactly one local meter structure.");
        }

        var source = inherited[0];
        var local = localMeters[0];
        var ratio = (double)source.Tempo / newBpm;
        var objectIds = ScopeObjectIds(graph, objects, segmentObjectId, allowed);
        var descendants = objectIds.Where(id => id != segmentObjectId).ToHashSet();
        var shared = parentObjects
            .Where(id => id != segmentObjectId
                && graph.Objects.TryGetValue(id, out var item)
                && item.Type == 10)
            .SelectMany(id => ScopeObjectIds(graph, objects, id, allowed))
            .Where(descendants.Contains)
            .Distinct()
            .Order()
            .ToArray();

        if (shared.Length > 0)
        {
            throw new InvalidDataException(
                $"Music segment {segmentObjectId} shares {shared.Length} timing object(s) with sibling segments. "
                + "An independent BPM change requires duplicating those Wwise objects first.");
        }

        var patches = new Dictionary<int, BnkRetimePatch>();
        AddDouble(patches, bank, "meter-grid", local.GridField, segmentObjectId, null,
            local.Grid, source.Grid * ratio);
        AddDouble(patches, bank, "meter-offset", local.OffsetField, segmentObjectId, null,
            local.GridOffset, source.GridOffset * ratio);
        AddFloat(patches, bank, "meter-tempo", local.TempoField, segmentObjectId,
            local.Tempo, checked((float)newBpm));
        AddByte(patches, bank, "meter-beats", local.BeatsField
                ?? throw new InvalidDataException($"Music segment {segmentObjectId} has no time-signature numerator field."), segmentObjectId,
            local.Beats, source.Beats);
        AddByte(patches, bank, "meter-beat-value", local.BeatValueField
                ?? throw new InvalidDataException($"Music segment {segmentObjectId} has no time-signature denominator field."), segmentObjectId,
            local.BeatValue, source.BeatValue);
        AddByte(patches, bank, "meter-enable", local.FlagField, segmentObjectId,
            local.Flag, 1);
        foreach (var id in objectIds)
        {
            AddTimelinePatches(patches, bank, objects[id], id, ratio);
        }

        return new BnkRetimePlan(
            segmentObjectId,
            fromBpm,
            newBpm,
            ratio,
            objectIds.Where(graph.Objects.ContainsKey)
                .SelectMany(id => graph.Objects[id].Media)
                .Select(media => media.MediaId)
                .Distinct()
                .Order()
                .ToArray(),
            objectIds,
            patches.Values.OrderBy(patch => patch.Offset).ToArray());
    }

    public static BnkTimingScope[] FindTimingScopes(string wwiserXmlPath, string? eventNameOrId = null)
    {
        var document = XDocument.Load(wwiserXmlPath);
        var graph = WwiserHircGraph.Load(wwiserXmlPath);
        var objects = ReadObjects(document);
        var allowed = eventNameOrId is null
            ? graph.Objects.Keys.ToHashSet()
            : graph.WithAncestors(graph.PlayScope(eventNameOrId).ReachableObjectIds);

        return allowed
            .Where(objects.ContainsKey)
            .Select(id => (Id: id, Meters: ActiveMeters(objects[id]).ToArray()))
            .Where(item => item.Meters.Length > 0)
            .Select(item =>
            {
                var objectIds = ScopeObjectIds(graph, objects, item.Id, allowed);
                var graphObject = graph.Objects[item.Id];

                return new BnkTimingScope(
                    item.Id,
                    graphObject.Type,
                    graphObject.Name,
                    item.Meters.Select(meter => (double)meter.Tempo).Distinct().Order().ToArray(),
                    objectIds,
                    objectIds
                        .SelectMany(id => graph.Objects[id].Media)
                        .Select(media => media.MediaId)
                        .Distinct()
                        .Order()
                        .ToArray());
            })
            .OrderBy(item => item.ObjectId)
            .ToArray();
    }

    public static byte[] Apply(byte[] bank, BnkRetimePlan plan)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(plan);
        var output = bank.ToArray();
        var previousEnd = 0;

        foreach (var patch in plan.Patches.OrderBy(item => item.Offset))
        {
            if (patch.Offset < previousEnd || patch.Offset < 0 || patch.Offset > output.Length - patch.OldBytes.Length)
            {
                throw new InvalidDataException($"Invalid or overlapping {patch.Kind} patch at 0x{patch.Offset:X}.");
            }

            var target = output.AsSpan(patch.Offset, patch.OldBytes.Length);
            if (!target.SequenceEqual(patch.OldBytes))
            {
                throw new InvalidDataException($"Bank bytes at 0x{patch.Offset:X} no longer match the wwiser {patch.Kind} value.");
            }

            patch.NewBytes.CopyTo(target);
            previousEnd = patch.Offset + patch.OldBytes.Length;
        }

        return output;
    }

    private static Dictionary<uint, XElement> ReadObjects(XDocument document)
    {
        var hirc = document.Descendants().FirstOrDefault(node => Is(node, "object", "obj") && Name(node) == "HircChunk")
            ?? throw new InvalidDataException("HircChunk was not found in the wwiser XML.");
        var loaded = hirc.Elements().FirstOrDefault(node => Is(node, "list", "lst") && Name(node) == "listLoadedItem")
            ?? throw new InvalidDataException("HIRC listLoadedItem was not found in the wwiser XML.");

        return loaded.Elements()
            .Where(node => Is(node, "object", "obj"))
            .Select(node => (Node: node, Id: DirectField(node, "ulID")))
            .Where(item => item.Id is not null)
            .ToDictionary(item => UInt(Value(item.Id!)!), item => item.Node);
    }

    private static uint[] ScopeObjectIds(
        WwiserHircGraph graph,
        IReadOnlyDictionary<uint, XElement> objects,
        uint scopeObjectId,
        IReadOnlySet<uint>? allowed)
    {
        var included = new HashSet<uint>();

        void Walk(uint id, bool root)
        {
            if (allowed is not null && !allowed.Contains(id) || included.Contains(id))
            {
                return;
            }

            if (!objects.TryGetValue(id, out var node))
            {
                return;
            }

            if (!root && ActiveMeters(node).Any())
            {
                return;
            }

            included.Add(id);

            if (graph.Objects.TryGetValue(id, out var item))
            {
                foreach (var child in item.ChildIds)
                {
                    Walk(child, false);
                }
            }
        }

        Walk(scopeObjectId, true);
        return included.Order().ToArray();
    }

    private static void AddTimelinePatches(
        Dictionary<int, BnkRetimePatch> patches,
        byte[] bank,
        XElement hircObject,
        uint objectId,
        double ratio)
    {
        foreach (var track in NamedObjects(hircObject, "AkTrackSrcInfo"))
        {
            var mediaId = DirectField(track, "sourceID") is { } source ? UInt(Value(source)!) : (uint?)null;
            AddScaledDouble(patches, bank, "track-play", DirectField(track, "fPlayAt"), objectId, mediaId, ratio);
            AddScaledDouble(patches, bank, "track-begin-trim", DirectField(track, "fBeginTrimOffset"), objectId, mediaId, ratio);
            AddScaledDouble(patches, bank, "track-end-trim", DirectField(track, "fEndTrimOffset"), objectId, mediaId, ratio);
        }

        foreach (var segment in NamedObjects(hircObject, "MusicSegmentInitialValues"))
        {
            AddScaledDouble(patches, bank, "segment-duration", DirectField(segment, "fDuration"), objectId, null, ratio);
        }

        foreach (var marker in NamedObjects(hircObject, "AkMusicMarkerWwise"))
        {
            AddScaledDouble(patches, bank, "marker-position", DirectField(marker, "fPosition"), objectId, null, ratio, skipZero: true);
        }
    }

    private static IEnumerable<MeterNode> ActiveMeters(XElement node) => Meters(node).Where(meter => meter.Flag == 1);

    private static IEnumerable<MeterNode> Meters(XElement node)
    {
        foreach (var parent in node.DescendantsAndSelf())
        {
            var children = parent.Elements().ToArray();
            for (var index = 0; index + 1 < children.Length; index++)
            {
                if (!Is(children[index], "object", "obj") || Name(children[index]) != "AkMeterInfo"
                    || !Is(children[index + 1], "field", "fld") || Name(children[index + 1]) != "bMeterInfoFlag")
                {
                    continue;
                }

                var meter = children[index];
                var grid = RequiredField(meter, "fGridPeriod");
                var offset = RequiredField(meter, "fGridOffset");
                var tempo = RequiredField(meter, "fTempo");
                var beats = DirectField(meter, "uTimeSigNumBeatsBar");
                var beatValue = DirectField(meter, "uTimeSigBeatValue");
                var flag = children[index + 1];

                yield return new MeterNode(
                    grid,
                    offset,
                    tempo,
                    beats,
                    beatValue,
                    flag,
                    Double(Value(grid)!),
                    Double(Value(offset)!),
                    float.Parse(Value(tempo)!, CultureInfo.InvariantCulture),
                    beats is null ? (byte)4 : checked((byte)Int(Value(beats)!)),
                    beatValue is null ? (byte)4 : checked((byte)Int(Value(beatValue)!)),
                    checked((byte)Int(Value(flag)!)));
            }
        }
    }

    private static void AddScaledDouble(
        Dictionary<int, BnkRetimePatch> patches,
        byte[] bank,
        string kind,
        XElement? field,
        uint objectId,
        uint? mediaId,
        double ratio,
        bool skipZero = false)
    {
        if (field is null)
        {
            return;
        }

        var oldValue = Double(Value(field)!);
        if (skipZero && oldValue == 0)
        {
            return;
        }

        AddDouble(patches, bank, kind, field, objectId, mediaId, oldValue, oldValue * ratio);
    }

    private static void AddDouble(
        Dictionary<int, BnkRetimePatch> patches,
        byte[] bank,
        string kind,
        XElement field,
        uint objectId,
        uint? mediaId,
        double oldValue,
        double newValue)
    {
        if (oldValue == newValue)
        {
            return;
        }

        Span<byte> oldBytes = stackalloc byte[8];
        Span<byte> newBytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(oldBytes, oldValue);
        BinaryPrimitives.WriteDoubleLittleEndian(newBytes, newValue);
        Add(patches, bank, new BnkRetimePatch(kind, Offset(field), objectId, mediaId, oldValue, newValue, oldBytes.ToArray(), newBytes.ToArray()));
    }

    private static void AddFloat(
        Dictionary<int, BnkRetimePatch> patches,
        byte[] bank,
        string kind,
        XElement field,
        uint objectId,
        float oldValue,
        float newValue)
    {
        if (oldValue == newValue)
        {
            return;
        }

        Span<byte> oldBytes = stackalloc byte[4];
        Span<byte> newBytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(oldBytes, oldValue);
        BinaryPrimitives.WriteSingleLittleEndian(newBytes, newValue);
        Add(patches, bank, new BnkRetimePatch(kind, Offset(field), objectId, null, oldValue, newValue, oldBytes.ToArray(), newBytes.ToArray()));
    }

    private static void AddByte(
        Dictionary<int, BnkRetimePatch> patches,
        byte[] bank,
        string kind,
        XElement field,
        uint objectId,
        byte oldValue,
        byte newValue)
    {
        if (oldValue == newValue)
        {
            return;
        }

        Add(patches, bank, new BnkRetimePatch(
            kind,
            Offset(field),
            objectId,
            null,
            oldValue,
            newValue,
            [oldValue],
            [newValue]));
    }

    private static void Add(Dictionary<int, BnkRetimePatch> patches, byte[] bank, BnkRetimePatch patch)
    {
        if (patch.Offset < 0 || patch.Offset > bank.Length - patch.OldBytes.Length
            || !bank.AsSpan(patch.Offset, patch.OldBytes.Length).SequenceEqual(patch.OldBytes))
        {
            throw new InvalidDataException($"wwiser {patch.Kind} value at 0x{patch.Offset:X} does not match the bank.");
        }

        if (patches.TryGetValue(patch.Offset, out var existing) && !existing.NewBytes.AsSpan().SequenceEqual(patch.NewBytes))
        {
            throw new InvalidDataException($"Conflicting timing patches target 0x{patch.Offset:X}.");
        }

        patches[patch.Offset] = patch;
    }

    private static IEnumerable<XElement> NamedObjects(XElement node, string name) =>
        node.Descendants().Where(item => Is(item, "object", "obj") && Name(item) == name);

    private static XElement RequiredField(XElement node, string name) => DirectField(node, name)
        ?? throw new InvalidDataException($"wwiser object '{Name(node)}' has no {name} field.");

    private static XElement? DirectField(XElement node, string name) => node.Elements()
        .FirstOrDefault(item => Is(item, "field", "fld") && Name(item) == name);

    private static string? Name(XElement node) => node.Attribute("name")?.Value ?? node.Attribute("na")?.Value;

    private static string? Value(XElement node) => node.Attribute("value")?.Value ?? node.Attribute("va")?.Value;

    private static int Offset(XElement node) => (node.Attribute("offset")?.Value ?? node.Attribute("of")?.Value) is { } value
        ? Int(value)
        : throw new InvalidDataException($"wwiser field '{Name(node)}' has no binary offset; generate a plain XML dump with '-d xml'.");

    private static bool Is(XElement node, string longName, string shortName) => node.Name.LocalName is var name && (name == longName || name == shortName);

    private static double Double(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static uint UInt(string value) => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static int Int(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private sealed record MeterNode(
        XElement GridField,
        XElement OffsetField,
        XElement TempoField,
        XElement? BeatsField,
        XElement? BeatValueField,
        XElement FlagField,
        double Grid,
        double GridOffset,
        float Tempo,
        byte Beats,
        byte BeatValue,
        byte Flag);
}
