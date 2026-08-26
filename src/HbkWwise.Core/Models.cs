namespace HbkWwise.Core;

public sealed record WwiseIndex(
    DateTimeOffset CreatedUtc,
    string[] Sources,
    BankRecord[] Banks,
    EventRecord[] Events,
    MediaRecord[] Media,
    WwiseName[] Names,
    PakSource[]? Paks = null,
    string? SourceFingerprint = null);

public sealed record PakSource(string Path, string WwiseRoot, int Priority = 0);

public sealed record PakAsset(string PakPath, string EntryPath, int Priority, bool IsEffective);

public sealed record BankRecord(
    uint Id,
    string Name,
    string Path,
    string Language,
    string Guid,
    PakAsset[]? Assets = null);

public sealed record EventRecord(
    uint Id,
    string Name,
    string Bank,
    string ObjectPath,
    string Guid,
    string DurationType,
    double? DurationMin,
    double? DurationMax,
    EventMediaReference[] Media);

public sealed record EventMediaReference(uint Id, string[] StatePaths);

public sealed record MediaRecord(
    uint Id,
    string Bank,
    string SourceName,
    string Path,
    string Language,
    bool IsStreamed,
    bool IsEmbedded,
    int? PrefetchSize,
    MediaUsage[] Uses,
    PakAsset[]? Assets = null)
{
    public bool IsWwiseMidi => System.IO.Path.GetExtension(SourceName).Equals(".mid", StringComparison.OrdinalIgnoreCase)
        || System.IO.Path.GetExtension(Path).Equals(".wmid", StringComparison.OrdinalIgnoreCase);

    public bool IsPlayableAudio => !IsWwiseMidi;

    public bool IsMusic => SourceName.StartsWith("Music\\", StringComparison.OrdinalIgnoreCase)
        || SourceName.StartsWith("Music/", StringComparison.OrdinalIgnoreCase);

    public string Storage => IsStreamed
        ? PrefetchSize is > 0 ? "streamed+prefetch" : "streamed"
        : "embedded";
}

public sealed record MediaUsage(uint EventId, string EventName, string[] StatePaths);

public sealed record WwiseName(uint Id, string Name);

public sealed record SearchOptions(
    string? Bank = null,
    string? Event = null,
    bool MusicOnly = false,
    bool? Streamed = null,
    int Limit = 50,
    string? Pak = null,
    string? Language = null);

public sealed record RelatedMedia(MediaRecord Media, int Score, string[] Reasons);

public sealed record AssetOverride(string Kind, string Name, string EntryPath, PakAsset[] Assets);
