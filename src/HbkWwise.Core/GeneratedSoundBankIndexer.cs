using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace HbkWwise.Core;

public sealed class GeneratedSoundBankIndexer
{
    public WwiseIndex Build(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var accumulator = new Accumulator();
        var fullPath = Path.GetFullPath(sourcePath);

        if (Directory.Exists(fullPath))
        {
            ReadDirectory(fullPath, accumulator);
        }
        else if (File.Exists(fullPath) && Path.GetExtension(fullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ReadArchive(fullPath, accumulator);
        }
        else if (File.Exists(fullPath))
        {
            using var stream = File.OpenRead(fullPath);
            ReadXml(stream, fullPath, accumulator);
        }
        else
        {
            throw new FileNotFoundException("XML source does not exist.", fullPath);
        }

        return accumulator.Finish();
    }

    public WwiseIndex BuildOverlay(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        if (directories.Count == 0)
        {
            throw new ArgumentException("At least one XML directory is required.", nameof(directories));
        }

        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories.Select(Path.GetFullPath))
        {
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(directory);
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".bnk.xml", StringComparison.OrdinalIgnoreCase)))
            {
                selected[Path.GetRelativePath(directory, path).Replace('\\', '/')] = path;
            }
        }

        var accumulator = new Accumulator();
        ReadFiles(selected.Select(pair => (pair.Value, pair.Key)), accumulator);

        return accumulator.Finish();
    }

    private static void ReadDirectory(string directory, Accumulator accumulator)
    {
        ReadFiles(Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".bnk.xml", StringComparison.OrdinalIgnoreCase))
            .Select(path => (path, Path.GetRelativePath(directory, path).Replace('\\', '/'))), accumulator);
    }

    private static void ReadFiles(IEnumerable<(string Path, string LogicalPath)> sourceFiles, Accumulator accumulator)
    {
        var files = sourceFiles.OrderBy(file => file.LogicalPath, StringComparer.OrdinalIgnoreCase).ToArray();

        var directoriesWithBanks = files
            .Where(file => !Path.GetFileName(file.LogicalPath).Equals("SoundbanksInfo.xml", StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetDirectoryName(file.LogicalPath) ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            using var stream = File.OpenRead(file.Path);
            var namesOnly = directoriesWithBanks.Contains(Path.GetDirectoryName(file.LogicalPath) ?? string.Empty)
                && Path.GetFileName(file.LogicalPath).Equals("SoundbanksInfo.xml", StringComparison.OrdinalIgnoreCase);
            ReadXml(stream, file.Path, accumulator, namesOnly);
        }
    }

    private static void ReadArchive(string archivePath, Accumulator accumulator)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(entry => entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.EndsWith(".bnk.xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var directoriesWithBanks = entries
            .Where(entry => !Path.GetFileName(entry.FullName).Equals("SoundbanksInfo.xml", StringComparison.OrdinalIgnoreCase))
            .Select(entry => Path.GetDirectoryName(entry.FullName) ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            using var stream = entry.Open();
            var namesOnly = directoriesWithBanks.Contains(Path.GetDirectoryName(entry.FullName) ?? string.Empty)
                && Path.GetFileName(entry.FullName).Equals("SoundbanksInfo.xml", StringComparison.OrdinalIgnoreCase);
            ReadXml(stream, $"{archivePath}:{entry.FullName}", accumulator, namesOnly);
        }
    }

    private static void ReadXml(Stream stream, string source, Accumulator accumulator, bool namesOnly = false)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CloseInput = false
        };

        using var reader = XmlReader.Create(stream, settings);
        var foundBank = false;
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            var name = reader.GetAttribute("Name");
            if (name is { Length: > 0 } && TryUInt(reader.GetAttribute("Id") ?? string.Empty, out var id)
                && WwiseHash.Fnv1(name) == id)
            {
                accumulator.Names[name] = id;
            }

            if (reader.LocalName == "SoundBank")
            {
                foundBank = true;
                if (!namesOnly)
                {
                    var bank = (XElement)XNode.ReadFrom(reader);
                    ParseBank(bank, accumulator);
                    continue;
                }
            }

            reader.Read();
        }

        if (foundBank)
        {
            accumulator.Sources.Add(source);
        }
    }

    private static void ParseBank(XElement element, Accumulator accumulator)
    {
        var bank = new BankRecord(
            RequiredUInt(element, "Id"),
            ChildValue(element, "ShortName"),
            ChildValue(element, "Path"),
            AttributeValue(element, "Language"),
            AttributeValue(element, "GUID"));
        accumulator.Banks.Add(bank);

        var guidNames = element.DescendantsAndSelf()
            .Select(TryReadStateDescriptor)
            .Where(item => item is not null)
            .Cast<StateDescriptor>()
            .GroupBy(item => item.Guid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var namedElement in element.DescendantsAndSelf())
        {
            if (!TryUInt(AttributeValue(namedElement, "Id"), out var id))
            {
                continue;
            }

            var name = AttributeValue(namedElement, "Name");
            if (name.Length == 0)
            {
                name = ChildValue(namedElement, "ShortName");
            }

            if (name.Length > 0 && WwiseHash.Fnv1(name) == id)
            {
                accumulator.Names[name] = id;
            }
        }

        if (bank.Language.Length > 0)
        {
            accumulator.Names[bank.Language] = WwiseHash.Fnv1(bank.Language);
        }

        var includedEvents = Child(element, "IncludedEvents");
        if (includedEvents is null)
        {
            return;
        }

        foreach (var eventElement in Children(includedEvents, "Event"))
        {
            ParseEvent(bank, eventElement, guidNames, accumulator);
        }
    }

    private static void ParseEvent(
        BankRecord bank,
        XElement element,
        IReadOnlyDictionary<string, StateDescriptor> guidNames,
        Accumulator accumulator)
    {
        var media = new Dictionary<uint, MutableEventMedia>();
        ReadEventMedia(element, "ReferencedStreamedFiles", streamed: true, embedded: false, media);
        ReadEventMedia(element, "IncludedMemoryFiles", streamed: false, embedded: true, media);

        foreach (var switchContainers in Children(element, "SwitchContainers"))
        {
            foreach (var container in Children(switchContainers, "SwitchContainer"))
            {
                ReadSwitchContainer(container, guidNames, [], media);
            }
        }

        var eventId = RequiredUInt(element, "Id");
        var eventName = AttributeValue(element, "Name");
        var eventRecord = new EventRecord(
            eventId,
            eventName,
            bank.Name,
            AttributeValue(element, "ObjectPath"),
            AttributeValue(element, "GUID"),
            AttributeValue(element, "DurationType"),
            OptionalDouble(element, "DurationMin"),
            OptionalDouble(element, "DurationMax"),
            media.Values
                .OrderBy(item => item.Id)
                .Select(item => new EventMediaReference(item.Id, item.StatePaths.Order(StringComparer.OrdinalIgnoreCase).ToArray()))
                .ToArray());

        accumulator.Events.Add(eventRecord);

        foreach (var item in media.Values)
        {
            accumulator.Media.Add(new MediaOccurrence(
                item.Id,
                bank.Name,
                item.SourceName,
                item.Path,
                item.Language,
                item.IsStreamed,
                item.IsEmbedded && !item.IsStreamed,
                item.PrefetchSize,
                eventId,
                eventName,
                item.StatePaths.ToArray()));
        }
    }

    private static void ReadEventMedia(
        XElement eventElement,
        string containerName,
        bool streamed,
        bool embedded,
        IDictionary<uint, MutableEventMedia> media)
    {
        foreach (var container in Children(eventElement, containerName))
        {
            foreach (var file in Children(container, "File"))
            {
                var id = RequiredUInt(file, "Id");
                if (!media.TryGetValue(id, out var item))
                {
                    item = new MutableEventMedia(id);
                    media.Add(id, item);
                }

                item.SourceName = Prefer(item.SourceName, ChildValue(file, "ShortName"));
                item.Path = Prefer(item.Path, ChildValue(file, "Path"));
                item.Language = Prefer(item.Language, AttributeValue(file, "Language"));
                item.IsStreamed |= streamed;
                item.IsEmbedded |= embedded && !streamed;
                item.PrefetchSize ??= OptionalInt(file, "PrefetchSize");
            }
        }
    }

    private static void ReadSwitchContainer(
        XElement container,
        IReadOnlyDictionary<string, StateDescriptor> guidNames,
        IReadOnlyList<string> parentPath,
        IDictionary<uint, MutableEventMedia> media)
    {
        var path = new List<string>(parentPath);
        var guid = NormalizeGuid(AttributeValue(container, "SwitchValue"));

        if (guidNames.TryGetValue(guid, out var state))
        {
            path.Add(state.Label);
        }
        else if (guid.Length > 0)
        {
            path.Add($"guid={guid}");
        }

        foreach (var mediaElement in Children(container, "Media"))
        {
            foreach (var file in Children(mediaElement, "File"))
            {
                var id = RequiredUInt(file, "Id");
                if (!media.TryGetValue(id, out var item))
                {
                    item = new MutableEventMedia(id);
                    media.Add(id, item);
                }

                if (path.Count > 0)
                {
                    item.StatePaths.Add(string.Join(" / ", path));
                }
            }
        }

        foreach (var children in Children(container, "Children"))
        {
            foreach (var child in Children(children, "SwitchContainer"))
            {
                ReadSwitchContainer(child, guidNames, path, media);
            }
        }
    }

    private static StateDescriptor? TryReadStateDescriptor(XElement element)
    {
        var guid = NormalizeGuid(AttributeValue(element, "GUID"));
        var name = AttributeValue(element, "Name");
        var objectPath = AttributeValue(element, "ObjectPath");

        if (guid.Length == 0 || name.Length == 0 || objectPath.Length == 0)
        {
            return null;
        }

        var parts = objectPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var group = parts.Length >= 2 ? parts[^2] : "state";

        return new StateDescriptor(guid, group, name);
    }

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement parent, string name) =>
        parent.Elements().Where(element => element.Name.LocalName == name);

    private static string ChildValue(XElement parent, string name) => Child(parent, name)?.Value ?? string.Empty;

    private static string AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value ?? string.Empty;

    private static uint RequiredUInt(XElement element, string attribute)
    {
        var raw = AttributeValue(element, attribute);
        return TryUInt(raw, out var value)
            ? value
            : throw new InvalidDataException($"Element {element.Name.LocalName} has invalid {attribute}='{raw}'.");
    }

    private static bool TryUInt(string raw, out uint value) =>
        uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static int? OptionalInt(XElement element, string child)
    {
        var raw = ChildValue(element, child);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? OptionalDouble(XElement element, string attribute)
    {
        var raw = AttributeValue(element, attribute);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string NormalizeGuid(string value) => value.Trim().Trim('{', '}');

    private static string Prefer(string existing, string candidate) => existing.Length > 0 ? existing : candidate;

    private sealed record StateDescriptor(string Guid, string Group, string Name)
    {
        public string Label => $"{Group}={Name}";
    }

    private sealed class MutableEventMedia(uint id)
    {
        public uint Id { get; } = id;
        public string SourceName { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public bool IsStreamed { get; set; }
        public bool IsEmbedded { get; set; }
        public int? PrefetchSize { get; set; }
        public HashSet<string> StatePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record MediaOccurrence(
        uint Id,
        string Bank,
        string SourceName,
        string Path,
        string Language,
        bool IsStreamed,
        bool IsEmbedded,
        int? PrefetchSize,
        uint EventId,
        string EventName,
        string[] StatePaths);

    private sealed class Accumulator
    {
        public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<BankRecord> Banks { get; } = [];
        public List<EventRecord> Events { get; } = [];
        public List<MediaOccurrence> Media { get; } = [];
        public Dictionary<string, uint> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public WwiseIndex Finish()
        {
            var banks = Banks
                .GroupBy(bank => (bank.Id, bank.Name))
                .Select(group => group.First())
                .OrderBy(bank => bank.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var events = Events
                .GroupBy(item => (item.Bank, item.Id))
                .Select(MergeEvents)
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Bank, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var media = Media
                .GroupBy(item => (item.Bank, item.Id))
                .Select(MergeMedia)
                .OrderBy(item => item.Id)
                .ThenBy(item => item.Bank, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var names = Names
                .Select(pair => new WwiseName(pair.Value, pair.Key))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new WwiseIndex(DateTimeOffset.UtcNow, Sources.Order(StringComparer.OrdinalIgnoreCase).ToArray(), banks, events, media, names);
        }

        private static EventRecord MergeEvents(IGrouping<(string Bank, uint Id), EventRecord> group)
        {
            var first = group.First();
            var media = group.SelectMany(item => item.Media)
                .GroupBy(item => item.Id)
                .Select(items => new EventMediaReference(
                    items.Key,
                    items.SelectMany(item => item.StatePaths).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()))
                .OrderBy(item => item.Id)
                .ToArray();

            return first with { Media = media };
        }

        private static MediaRecord MergeMedia(IGrouping<(string Bank, uint Id), MediaOccurrence> group)
        {
            var first = group.First();
            var uses = group
                .GroupBy(item => (item.EventId, item.EventName))
                .Select(items => new MediaUsage(
                    items.Key.EventId,
                    items.Key.EventName,
                    items.SelectMany(item => item.StatePaths).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()))
                .OrderBy(item => item.EventName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new MediaRecord(
                first.Id,
                first.Bank,
                group.Select(item => item.SourceName).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
                group.Select(item => item.Path).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
                group.Select(item => item.Language).FirstOrDefault(value => value.Length > 0) ?? string.Empty,
                group.Any(item => item.IsStreamed),
                group.Any(item => item.IsEmbedded),
                group.Select(item => item.PrefetchSize).FirstOrDefault(value => value is not null),
                uses);
        }
    }
}
