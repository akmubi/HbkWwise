using System.Globalization;
using System.Xml.Linq;

namespace HbkWwise.Core;

public sealed record WwiserObjectMedia(
    uint MediaId,
    int? StreamType,
    int? MemorySize,
    double? DurationMs,
    int[] SourceIdOffsets,
    int[] MemorySizeOffsets);

public enum WwiserActionKind
{
    Unknown,
    Play,
    Stop,
    SetState,
    Trigger
}

public sealed record WwiserEventAction(
    uint ActionId,
    WwiserActionKind Kind,
    string ObjectClass,
    uint[] TargetIds);

public sealed record WwiserEventProgram(uint EventId, WwiserEventAction[] Actions);

public sealed record WwiserFlowTarget(uint ObjectId, uint[] Keys, int Order);

public sealed record WwiserHircObject(
    uint Id,
    int Type,
    string Name,
    uint[] ChildIds,
    uint[] ActionIds,
    uint[] TargetIds,
    WwiserObjectMedia[] Media,
    string Behavior,
    uint[] FlowArgumentIds,
    WwiserFlowTarget[] FlowTargets,
    uint? ParentId = null);

public sealed record WwiserEventMedia(
    uint MediaId,
    double? MaxDurationMs,
    int[] StreamTypes,
    int[] MemorySizes,
    uint[] ObjectIds);

public sealed record WwiserEventScope(
    uint EventId,
    uint[] ActionIds,
    uint[] TargetIds,
    uint[] ReachableObjectIds,
    WwiserEventMedia[] Media);

public sealed class WwiserHircGraph
{
    private WwiserHircGraph(IReadOnlyDictionary<uint, WwiserHircObject> objects) => Objects = objects;

    public IReadOnlyDictionary<uint, WwiserHircObject> Objects { get; }

    public static WwiserHircGraph Load(string xmlPath) => Parse(XDocument.Load(xmlPath));

    public static WwiserHircGraph ParseText(string xml) => Parse(XDocument.Parse(xml));

    public int[] MemorySizeOffsets(uint mediaId, IReadOnlySet<uint>? objectIds = null) => Objects.Values
        .Where(item => objectIds is null || objectIds.Contains(item.Id))
        .SelectMany(item => item.Media)
        .Where(item => item.MediaId == mediaId)
        .SelectMany(item => item.MemorySizeOffsets)
        .Distinct()
        .Order()
        .ToArray();

    public int[] MediaReferenceOffsets(uint mediaId, IReadOnlySet<uint>? objectIds = null) => Objects.Values
        .Where(item => objectIds is null || objectIds.Contains(item.Id))
        .SelectMany(item => item.Media)
        .Where(item => item.MediaId == mediaId)
        .SelectMany(item => item.SourceIdOffsets)
        .Distinct()
        .Order()
        .ToArray();

    public WwiserEventProgram EventProgram(string nameOrId)
    {
        var eventId = ParseId(nameOrId);
        if (!Objects.TryGetValue(eventId, out var eventObject))
        {
            throw new InvalidDataException($"Event '{nameOrId}' (ShortID {eventId}) was not found in the HIRC dump.");
        }

        if (eventObject.Type != 4)
        {
            throw new InvalidDataException($"HIRC object {eventId} is type {eventObject.Type}, not an Event (4).");
        }

        var actions = eventObject.ActionIds.Distinct().Select(actionId =>
        {
            if (!Objects.TryGetValue(actionId, out var action))
            {
                throw new InvalidDataException($"Event {eventId} references missing action {actionId}.");
            }

            return new WwiserEventAction(
                actionId,
                ActionKind(action.Name),
                action.Name,
                action.TargetIds.Where(id => id != 0).Distinct().ToArray());
        }).ToArray();
        if (actions.Length == 0)
        {
            throw new InvalidDataException($"Event {eventId} contains no action references.");
        }

        return new WwiserEventProgram(eventId, actions);
    }

    public WwiserEventScope EventScope(string nameOrId)
    {
        var program = EventProgram(nameOrId);
        var targetIds = program.Actions.SelectMany(action => action.TargetIds).Distinct().ToArray();

        if (targetIds.Length == 0)
        {
            throw new InvalidDataException($"Event {program.EventId} actions contain no non-zero targets.");
        }

        return Scope(program.EventId, program.Actions.Select(action => action.ActionId).ToArray(), targetIds);
    }

    public WwiserEventScope PlayScope(string nameOrId)
    {
        var program = EventProgram(nameOrId);
        var playActions = program.Actions.Where(action => action.Kind == WwiserActionKind.Play).ToArray();

        if (playActions.Length == 0)
        {
            playActions = program.Actions.Where(action => action.Kind == WwiserActionKind.Unknown).ToArray();
        }

        return Scope(
            program.EventId,
            playActions.Select(action => action.ActionId).ToArray(),
            playActions.SelectMany(action => action.TargetIds).Distinct().ToArray());
    }

    public HashSet<uint> WithAncestors(IEnumerable<uint> objectIds)
    {
        ArgumentNullException.ThrowIfNull(objectIds);
        var expanded = objectIds.ToHashSet();
        var pending = new Stack<uint>(expanded);

        while (pending.TryPop(out var id))
        {
            if (!Objects.TryGetValue(id, out var item)
                || item.ParentId is not { } parentId
                || parentId == 0
                || !expanded.Add(parentId))
            {
                continue;
            }

            pending.Push(parentId);
        }

        return expanded;
    }

    private WwiserEventScope Scope(uint eventId, uint[] actionIds, uint[] targetIds)
    {
        var reachable = new HashSet<uint>();
        var pending = new Stack<uint>(targetIds);

        while (pending.TryPop(out var id))
        {
            if (!reachable.Add(id) || !Objects.TryGetValue(id, out var item))
            {
                continue;
            }

            foreach (var child in item.ChildIds)
            {
                if (child != 0 && !reachable.Contains(child))
                {
                    pending.Push(child);
                }
            }
        }

        var media = reachable
            .Where(Objects.ContainsKey)
            .SelectMany(id => Objects[id].Media.Select(item => (ObjectId: id, Item: item)))
            .GroupBy(item => item.Item.MediaId)
            .Select(group => new WwiserEventMedia(
                group.Key,
                group.Max(item => item.Item.DurationMs),
                group.Select(item => item.Item.StreamType).OfType<int>().Distinct().Order().ToArray(),
                group.Select(item => item.Item.MemorySize).OfType<int>().Distinct().Order().ToArray(),
                group.Select(item => item.ObjectId).Distinct().Order().ToArray()))
            .OrderBy(item => item.MediaId)
            .ToArray();
        return new WwiserEventScope(eventId, actionIds, targetIds, reachable.Order().ToArray(), media);
    }

    private static WwiserHircGraph Parse(XDocument document)
    {
        var hirc = document.Descendants().FirstOrDefault(node => Is(node, "object", "obj") && Name(node) == "HircChunk")
            ?? throw new InvalidDataException("HircChunk was not found in the wwiser XML.");
        var loaded = hirc.Elements().FirstOrDefault(node => Is(node, "list", "lst") && Name(node) == "listLoadedItem")
            ?? throw new InvalidDataException("HIRC listLoadedItem was not found in the wwiser XML.");
        var objects = new Dictionary<uint, WwiserHircObject>();

        foreach (var node in loaded.Elements().Where(node => Is(node, "object", "obj")))
        {
            var idText = DirectField(node, "ulID");
            if (idText is null)
            {
                continue;
            }

            var id = UInt(idText);
            var type = FieldValues(node, "eHircType").Select(Int).FirstOrDefault();
            var flow = ReadFlow(node, type);
            var item = new WwiserHircObject(
                id,
                type,
                Name(node) ?? string.Empty,
                FieldValues(node, "ulChildID").Select(UInt).Where(value => value != 0).Distinct().ToArray(),
                FieldValues(node, "ulActionID").Select(UInt).Where(value => value != 0).Distinct().ToArray(),
                FieldValues(node, "idExt").Select(UInt).Where(value => value != 0).Distinct().ToArray(),
                ReadMedia(node),
                ReadBehavior(node, type),
                flow.Arguments,
                flow.Targets,
                FirstUInt(node, "DirectParentID"));

            if (!objects.TryAdd(id, item))
            {
                throw new InvalidDataException($"Duplicate HIRC object ID {id}.");
            }
        }

        return new WwiserHircGraph(objects);
    }

    private static WwiserObjectMedia[] ReadMedia(XElement hircObject)
    {
        var media = new Dictionary<uint, MediaAccumulator>();
        foreach (var source in NamedObjects(hircObject, "AkBankSourceData"))
        {
            var idField = Fields(source, "sourceID").FirstOrDefault();
            var id = idField is null || Value(idField) is not { } idValue ? (uint?)null : UInt(idValue);

            if (id is null or 0)
            {
                continue;
            }

            if (!media.TryGetValue(id.Value, out var item))
            {
                item = media[id.Value] = new MediaAccumulator();
            }

            item.Stream = FirstInt(source, "StreamType");
            if (Offset(idField!) is { } sourceOffset)
            {
                item.SourceOffsets.Add(sourceOffset);
            }

            var memoryField = Fields(source, "uInMemoryMediaSize").FirstOrDefault();
            item.Memory = memoryField is null || Value(memoryField) is not { } memory ? null : Int(memory);
            if (memoryField is not null && Offset(memoryField) is { } offset)
            {
                item.MemoryOffsets.Add(offset);
            }
        }

        foreach (var track in NamedObjects(hircObject, "AkTrackSrcInfo"))
        {
            var idField = Fields(track, "sourceID").FirstOrDefault();
            var id = idField is null || Value(idField) is not { } idValue ? (uint?)null : UInt(idValue);

            if (id is null or 0)
            {
                continue;
            }

            if (!media.TryGetValue(id.Value, out var item))
            {
                item = media[id.Value] = new MediaAccumulator();
            }

            if (Offset(idField!) is { } sourceOffset)
            {
                item.SourceOffsets.Add(sourceOffset);
            }

            var duration = FieldValues(track, "fSrcDuration")
                .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
                .FirstOrDefault();
            if (item.Duration is null || duration > item.Duration.Value)
            {
                item.Duration = duration;
            }
        }

        return media.Select(item => new WwiserObjectMedia(
            item.Key,
            item.Value.Stream,
            item.Value.Memory,
            item.Value.Duration,
            item.Value.SourceOffsets.Order().ToArray(),
            item.Value.MemoryOffsets.Order().ToArray())).ToArray();
    }

    private static WwiserActionKind ActionKind(string name) => name switch
    {
        var value when value.Contains("ActionPlay", StringComparison.OrdinalIgnoreCase) => WwiserActionKind.Play,
        var value when value.Contains("ActionStop", StringComparison.OrdinalIgnoreCase) => WwiserActionKind.Stop,
        var value when value.Contains("ActionSetState", StringComparison.OrdinalIgnoreCase) => WwiserActionKind.SetState,
        var value when value.Contains("ActionTrigger", StringComparison.OrdinalIgnoreCase) => WwiserActionKind.Trigger,
        _ => WwiserActionKind.Unknown
    };

    private static string ReadBehavior(XElement node, int type)
    {
        if (type == 12)
        {
            var arguments = NamedObjects(node, "AkGameSync").Select(item =>
            {
                var group = FirstUInt(item, "ulGroup") ?? 0;
                return $"{DisplayValue(item, "eGroupType")} {group}";
            }).ToArray();
            var destinations = FieldValues(node, "audioNodeId")
                .Select(value => TryUInt(value, out var id) ? id : 0)
                .Where(id => id != 0)
                .Distinct()
                .Count();

            return "Switch decision tree\n"
                + $"Mode: {DisplayValue(node, "uMode")}\n"
                + $"Depth: {FirstInt(node, "uTreeDepth") ?? 0} game-sync levels\n"
                + $"Arguments: {(arguments.Length == 0 ? "none" : string.Join(" / ", arguments))}\n"
                + $"Destinations: {destinations}\n"
                + $"Continue playback: {(FirstInt(node, "bIsContinuePlayback") == 1 ? "yes" : "no")}\n"
                + $"Transition rules: {FirstInt(node, "numRules") ?? 0}";
        }

        if (type == 13)
        {
            var playlistItems = NamedObjects(node, "AkMusicRanSeqPlaylistItem").ToArray();
            var root = playlistItems.FirstOrDefault();
            var destinations = playlistItems.Count(item => FirstUInt(item, "SegmentID") is > 0);

            return "Music playlist\n"
                + $"Mode: {(root is null ? "unknown" : DisplayValue(root, "eRSType"))}\n"
                + $"Entries: {destinations}\n"
                + $"Loop: {(root is null ? 0 : FirstInt(root, "Loop") ?? 0)}\n"
                + $"Shuffle: {(root is not null && FirstInt(root, "bIsShuffle") == 1 ? "yes" : "no")}\n"
                + $"Weighted: {(root is not null && FirstInt(root, "bIsUsingWeight") == 1 ? "yes" : "no")}\n"
                + $"Avoid immediate repeats: {(root is null ? 0 : FirstInt(root, "wAvoidRepeatCount") ?? 0)}\n"
                + $"Transition rules: {FirstInt(node, "numRules") ?? 0}";
        }

        return string.Empty;
    }

    private static (uint[] Arguments, WwiserFlowTarget[] Targets) ReadFlow(XElement node, int type)
    {
        if (type == 12)
        {
            var arguments = NamedObjects(node, "AkGameSync")
                .Select(item => DirectUInt(item, "ulGroup"))
                .Where(id => id != 0)
                .ToArray();
            var targets = NamedObjects(node, "Node")
                .Select((item, order) =>
                {
                    var target = DirectUInt(item, "audioNodeId");
                    var keys = item.AncestorsAndSelf()
                        .Where(parent => Is(parent, "object", "obj") && Name(parent) == "Node")
                        .Reverse()
                        .Select(parent => DirectUInt(parent, "key"))
                        .TakeLast(arguments.Length)
                        .ToArray();
                    return new WwiserFlowTarget(target, keys, order);
                })
                .Where(item => item.ObjectId != 0)
                .Select((item, order) => item with { Order = order + 1 })
                .ToArray();

            return (arguments, targets);
        }

        if (type == 13)
        {
            var targets = NamedObjects(node, "AkMusicRanSeqPlaylistItem")
                .Select((item, order) => new WwiserFlowTarget(DirectUInt(item, "SegmentID"), [], order))
                .Where(item => item.ObjectId != 0)
                .Select((item, order) => item with { Order = order + 1 })
                .ToArray();
            return ([], targets);
        }

        return ([], []);
    }

    private static IEnumerable<XElement> NamedObjects(XElement node, string name) =>
        node.Descendants().Where(item => Is(item, "object", "obj") && Name(item) == name);

    private static IEnumerable<XElement> Fields(XElement node, string name) => node.Descendants()
        .Where(item => Is(item, "field", "fld") && Name(item) == name);

    private static IEnumerable<string> FieldValues(XElement node, string name) => Fields(node, name)
        .Select(Value)
        .OfType<string>();

    private static string? DirectField(XElement node, string name) => node.Elements()
        .FirstOrDefault(item => Is(item, "field", "fld") && Name(item) == name) is { } field ? Value(field) : null;

    private static uint DirectUInt(XElement node, string name) => DirectField(node, name) is { } value ? UInt(value) : 0;

    private static uint? FirstUInt(XElement node, string name) => FieldValues(node, name).Select(UInt).Cast<uint?>().FirstOrDefault();

    private static int? FirstInt(XElement node, string name) => FieldValues(node, name).Select(Int).Cast<int?>().FirstOrDefault();

    private static string DisplayValue(XElement node, string name)
    {
        var field = Fields(node, name).FirstOrDefault();
        if (field is null)
        {
            return "unknown";
        }

        var formatted = field.Attribute("valuefmt")?.Value ?? field.Attribute("vf")?.Value;
        if (formatted is not null && formatted.IndexOf('[') is var start && start >= 0
            && formatted.LastIndexOf(']') is var end && end > start)
        {
            return formatted[(start + 1)..end];
        }

        return Value(field) ?? "unknown";
    }

    private static string? Name(XElement node) => node.Attribute("name")?.Value ?? node.Attribute("na")?.Value;

    private static string? Value(XElement node) => node.Attribute("value")?.Value ?? node.Attribute("va")?.Value;

    private static int? Offset(XElement node) => (node.Attribute("offset")?.Value ?? node.Attribute("of")?.Value) is { } value
        ? Int(value)
        : null;

    private static bool Is(XElement node, string longName, string shortName) => node.Name.LocalName is var name && (name == longName || name == shortName);

    private static uint ParseId(string value) => TryUInt(value, out var id) ? id : WwiseHash.Fnv1(value);

    private static uint UInt(string value) => TryUInt(value, out var result)
        ? result
        : throw new InvalidDataException($"Invalid wwiser integer '{value}'.");

    private static int Int(string value) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static bool TryUInt(string value, out uint result) => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result)
        : uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private sealed class MediaAccumulator
    {
        public int? Stream { get; set; }

        public int? Memory { get; set; }

        public double? Duration { get; set; }

        public HashSet<int> SourceOffsets { get; } = [];

        public HashSet<int> MemoryOffsets { get; } = [];
    }
}

public static class WwiserClient
{
    public static async Task<string> DumpXmlAsync(
        string bankPath,
        string outputPath,
        string? wwiserPath = null,
        string? pythonPath = null,
        string? namesPath = null,
        CancellationToken cancellationToken = default)
    {
        var bank = ExistingFile(bankPath, "BNK");
        var wwiser = FindWwiser(wwiserPath);
        var python = FindPython(pythonPath);
        var output = Path.GetFullPath(outputPath);

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = $"{output}.{Guid.NewGuid():N}.tmp.xml";
        var dumpName = temporary[..^4];
        var arguments = new List<string>();

        if (Path.GetFileName(python).Equals("py.exe", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("-3");
        }

        arguments.Add(wwiser);
        arguments.Add(bank);
        arguments.AddRange(["-d", "xml", "-dn", dumpName]);
        if (!string.IsNullOrWhiteSpace(namesPath))
        {
            arguments.AddRange(["-nl", ExistingFile(namesPath, "wwnames")]);
        }

        try
        {
            await RepakArchive.RunAsync(python, arguments, cancellationToken, Path.GetDirectoryName(wwiser));
            if (!File.Exists(temporary))
            {
                throw new InvalidOperationException("wwiser completed without creating its XML dump.");
            }

            File.Move(temporary, output, true);
            return output;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static string FindWwiser(string? configuredPath = null)
    {
        var candidates = new[]
        {
            configuredPath,
            Environment.GetEnvironmentVariable("HBKWWISE_WWISER"),
            Path.Combine(Environment.CurrentDirectory, "wwiser.pyz"),
            Path.Combine(Environment.CurrentDirectory, "..", "wwiser.pyz"),
            Path.Combine(AppContext.BaseDirectory, "tools", "win-x64", "wwiser.pyz"),
            Path.Combine(AppContext.BaseDirectory, "wwiser.pyz")
        };
        var found = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));

        return found is null
            ? throw new FileNotFoundException("wwiser.pyz was not found. Set HBKWWISE_WWISER or pass --wwiser.")
            : Path.GetFullPath(found);
    }

    public static string FindPython(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return ExistingFile(configuredPath, "Python");
        }

        var environment = Environment.GetEnvironmentVariable("HBKWWISE_PYTHON");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return ExistingFile(environment, "Python");
        }

        foreach (var fileName in new[] { "py.exe", "python.exe", "python3.exe" })
        {
            try
            {
                return RepakArchive.FindTool(null, "HBKWWISE_PYTHON", fileName);
            }
            catch (FileNotFoundException)
            {
            }
        }

        throw new FileNotFoundException("Python was not found. Set HBKWWISE_PYTHON or pass --python.");
    }

    private static string ExistingFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath) ? fullPath : throw new FileNotFoundException($"{description} file not found.", fullPath);
    }
}
