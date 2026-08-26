using System.Globalization;
using System.Xml.Linq;

namespace HbkWwise.Core;

public enum BnkDurationFit
{
    Match,
    TooShort,
    Longer,
    NotUsed
}

public sealed record BnkClipUsage(
    uint ObjectId,
    uint MediaId,
    double PlayAtMs,
    double BeginTrimMs,
    double EndTrimMs,
    double SourceDurationMs);

public sealed record BnkMediaDurationCheck(
    uint MediaId,
    double ReplacementDurationMs,
    double[] AuthoredDurationsMs,
    int ClipUses,
    BnkDurationFit Fit)
{
    public double? DifferenceMs => AuthoredDurationsMs.Length == 0
        ? null
        : ReplacementDurationMs - AuthoredDurationsMs.Max();
}

public sealed record BnkDurationValidation(
    uint ScopeObjectId,
    BnkClipUsage[] ClipUsages,
    BnkMediaDurationCheck[] Checks)
{
    public bool HasErrors => Checks.Any(item => item.Fit == BnkDurationFit.TooShort);
}

public static class BnkDurationValidator
{
    public static BnkDurationValidation Validate(
        string wwiserXmlPath,
        uint scopeObjectId,
        IReadOnlyDictionary<uint, double> replacementDurationsMs,
        double toleranceMs = 1,
        string? eventNameOrId = null)
    {
        ArgumentNullException.ThrowIfNull(replacementDurationsMs);
        if (toleranceMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceMs));
        }

        var scope = BnkRetimer.FindTimingScopes(wwiserXmlPath, eventNameOrId)
            .SingleOrDefault(item => item.ObjectId == scopeObjectId)
            ?? throw new InvalidDataException($"Active timing scope {scopeObjectId} was not found.");
        var allowed = scope.ObjectIds.ToHashSet();
        var usages = ReadObjects(XDocument.Load(wwiserXmlPath))
            .Where(item => allowed.Contains(item.Id))
            .SelectMany(item => NamedObjects(item.Node, "AkTrackSrcInfo")
                .Select(track => ReadUsage(item.Id, track)))
            .Where(item => item is not null)
            .Cast<BnkClipUsage>()
            .OrderBy(item => item.MediaId)
            .ThenBy(item => item.ObjectId)
            .ToArray();
        var checks = replacementDurationsMs.OrderBy(item => item.Key)
            .Select(item => Check(item.Key, item.Value, usages, toleranceMs))
            .ToArray();

        return new BnkDurationValidation(scopeObjectId, usages, checks);
    }

    private static BnkMediaDurationCheck Check(
        uint mediaId,
        double replacementDurationMs,
        IReadOnlyCollection<BnkClipUsage> usages,
        double toleranceMs)
    {
        var matching = usages.Where(item => item.MediaId == mediaId).ToArray();
        var durations = matching.Select(item => item.SourceDurationMs).Distinct().Order().ToArray();
        var fit = durations.Length == 0
            ? BnkDurationFit.NotUsed
            : replacementDurationMs < durations.Max() - toleranceMs
                ? BnkDurationFit.TooShort
                : replacementDurationMs > durations.Min() + toleranceMs
                    ? BnkDurationFit.Longer
                    : BnkDurationFit.Match;

        return new BnkMediaDurationCheck(mediaId, replacementDurationMs, durations, matching.Length, fit);
    }

    private static BnkClipUsage? ReadUsage(uint objectId, XElement track)
    {
        var source = DirectField(track, "sourceID");
        var duration = DirectField(track, "fSrcDuration");

        if (source is null || duration is null)
        {
            return null;
        }

        var mediaId = UInt(Value(source)!);
        if (mediaId == 0)
        {
            return null;
        }

        return new BnkClipUsage(
            objectId,
            mediaId,
            DoubleValue(track, "fPlayAt"),
            DoubleValue(track, "fBeginTrimOffset"),
            DoubleValue(track, "fEndTrimOffset"),
            Double(Value(duration)!));
    }

    private static (uint Id, XElement Node)[] ReadObjects(XDocument document)
    {
        var hirc = document.Descendants().FirstOrDefault(node => Is(node, "object", "obj") && Name(node) == "HircChunk")
            ?? throw new InvalidDataException("HircChunk was not found in the wwiser XML.");
        var loaded = hirc.Elements().FirstOrDefault(node => Is(node, "list", "lst") && Name(node) == "listLoadedItem")
            ?? throw new InvalidDataException("HIRC listLoadedItem was not found in the wwiser XML.");

        return loaded.Elements()
            .Where(node => Is(node, "object", "obj"))
            .Select(node => (Node: node, Field: DirectField(node, "ulID")))
            .Where(item => item.Field is not null)
            .Select(item => (UInt(Value(item.Field!)!), item.Node))
            .ToArray();
    }

    private static double DoubleValue(XElement node, string name) => DirectField(node, name) is { } field
        ? Double(Value(field)!)
        : 0;

    private static IEnumerable<XElement> NamedObjects(XElement node, string name) =>
        node.Descendants().Where(item => Is(item, "object", "obj") && Name(item) == name);

    private static XElement? DirectField(XElement node, string name) => node.Elements()
        .FirstOrDefault(item => Is(item, "field", "fld") && Name(item) == name);

    private static string? Name(XElement node) => node.Attribute("name")?.Value ?? node.Attribute("na")?.Value;

    private static string? Value(XElement node) => node.Attribute("value")?.Value ?? node.Attribute("va")?.Value;

    private static bool Is(XElement node, string longName, string shortName) =>
        node.Name.LocalName is var name && (name == longName || name == shortName);

    private static double Double(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static uint UInt(string value) => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
