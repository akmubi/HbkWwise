using System.Text.RegularExpressions;

namespace HbkWwise.Core;

public static partial class IndexQueries
{
    public static IReadOnlyList<MediaRecord> Search(this WwiseIndex index, string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        options ??= new SearchOptions();
        var terms = Terms(query);

        return index.Media
            .Where(item => !options.MusicOnly || item.IsMusic)
            .Where(item => options.Streamed is null || item.IsStreamed == options.Streamed)
            .Where(item => options.Bank is null || item.Bank.Contains(options.Bank, StringComparison.OrdinalIgnoreCase))
            .Where(item => options.Language is null || item.Language.Contains(options.Language, StringComparison.OrdinalIgnoreCase))
            .Where(item => options.Event is null || item.Uses.Any(use => use.EventName.Contains(options.Event, StringComparison.OrdinalIgnoreCase)))
            .Where(item => options.Pak is null || MatchesPak(item.Assets, options.Pak))
            .Select(item => (Item: item, Score: SearchScore(item, terms)))
            .Where(result => terms.Length == 0 || result.Score >= terms.Length)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Item.SourceName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, options.Limit))
            .Select(result => result.Item)
            .ToArray();
    }

    public static IReadOnlyList<MediaRecord> FindMedia(this WwiseIndex index, uint id) =>
        index.Media.Where(item => item.Id == id).OrderBy(item => item.Bank, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<EventRecord> FindEvents(this WwiseIndex index, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var byId = uint.TryParse(value, out var id);

        return index.Events
            .Where(item => byId ? item.Id == id : item.Name.Contains(value, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Bank, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<AssetOverride> Overrides(this WwiseIndex index) => index.Banks
        .Where(bank => bank.Assets is { Length: > 1 })
        .Select(bank => new AssetOverride("bank", bank.Name, bank.Assets![0].EntryPath, bank.Assets))
        .Concat(index.Media
            .Where(media => media.Assets is { Length: > 1 })
            .Select(media => new AssetOverride("media", $"{media.Id} {media.SourceName}", media.Assets![0].EntryPath, media.Assets)))
        .GroupBy(item => $"{item.Kind}\0{item.EntryPath}", StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.EntryPath, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static PakAsset? EffectiveAsset(this MediaRecord media) =>
        media.Assets?.FirstOrDefault(asset => asset.IsEffective);

    public static PakAsset? EffectiveAsset(this BankRecord bank) =>
        bank.Assets?.FirstOrDefault(asset => asset.IsEffective);

    public static IReadOnlyList<RelatedMedia> Related(this WwiseIndex index, uint id, int limit = 30)
    {
        var targets = index.FindMedia(id);
        if (targets.Count == 0)
        {
            return [];
        }

        var targetEvents = targets.SelectMany(item => item.Uses).Select(item => item.EventId).ToHashSet();
        var targetStates = targets.SelectMany(item => item.Uses).SelectMany(item => item.StatePaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetBanks = targets.Select(item => item.Bank).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetTokens = targets.SelectMany(item => Terms(Path.GetFileNameWithoutExtension(item.SourceName))).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return index.Media
            .Where(item => item.Id != id)
            .Select(item => ScoreRelated(item, targetEvents, targetStates, targetBanks, targetTokens))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Media.SourceName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    private static int SearchScore(MediaRecord item, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return 0;
        }

        var source = item.SourceName;
        var context = string.Join(' ', item.Id, item.Bank, item.Language, item.Path,
            string.Join(' ', item.Uses.Select(use => use.EventName)),
            string.Join(' ', item.Uses.SelectMany(use => use.StatePaths)));
        var matched = 0;
        var bonus = 0;

        foreach (var term in terms)
        {
            if (source.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
                bonus += 4;
            }
            else if (context.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                matched++;
                bonus += 1;
            }
        }

        return matched == terms.Count ? matched + bonus : matched;
    }

    private static RelatedMedia ScoreRelated(
        MediaRecord media,
        IReadOnlySet<uint> eventIds,
        IReadOnlySet<string> statePaths,
        IReadOnlySet<string> banks,
        IReadOnlySet<string> targetTokens)
    {
        var reasons = new List<string>();
        var score = 0;

        if (media.Uses.Any(use => use.StatePaths.Any(statePaths.Contains)))
        {
            score += 100;
            reasons.Add("same state path");
        }

        if (media.Uses.Any(use => eventIds.Contains(use.EventId)))
        {
            score += 50;
            reasons.Add("same event");
        }

        if (banks.Contains(media.Bank))
        {
            score += 10;
            reasons.Add("same bank");
        }

        var sharedTokens = Terms(Path.GetFileNameWithoutExtension(media.SourceName))
            .Count(token => token.Length >= 3 && targetTokens.Contains(token));
        if (sharedTokens > 0)
        {
            score += Math.Min(sharedTokens * 3, 15);
            reasons.Add($"{sharedTokens} shared name token{(sharedTokens == 1 ? string.Empty : "s")}");
        }

        return new RelatedMedia(media, score, reasons.ToArray());
    }

    private static bool MatchesPak(PakAsset[]? assets, string value) => assets?.Any(asset =>
    {
        if (!asset.IsEffective)
        {
            return false;
        }

        var name = Path.GetFileName(asset.PakPath);
        return value.Equals("base", StringComparison.OrdinalIgnoreCase)
                && name.Equals("Hibiki-WindowsNoEditor.pak", StringComparison.OrdinalIgnoreCase)
            || value.Equals("update", StringComparison.OrdinalIgnoreCase)
                && name.Equals("Hibiki-WindowsNoEditor_0_P.pak", StringComparison.OrdinalIgnoreCase)
            || name.Contains(value, StringComparison.OrdinalIgnoreCase)
            || asset.PakPath.Contains(value, StringComparison.OrdinalIgnoreCase);
    }) == true;

    private static string[] Terms(string? value) => value is null
        ? []
        : TokenPattern().Matches(value)
            .Select(match => match.Value)
            .Where(term => term.Length > 0)
            .ToArray();

    [GeneratedRegex("[A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

}
