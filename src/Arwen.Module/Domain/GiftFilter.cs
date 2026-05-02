using Mithril.Shared.Storage;

namespace Arwen.Domain;

/// <summary>
/// Verifies that a real inventory item passes every filter-style keyword
/// across all matched NPC preferences. CDN template matching is over-inclusive
/// for filter keywords like MinRarity / MinValue / Rarity / Crafted — those are
/// checked against the actual <see cref="StorageItem"/> properties here.
/// </summary>
public static class GiftFilter
{
    public static bool Passes(StorageItem item, IReadOnlyList<MatchedPreference> matchedPrefs)
    {
        foreach (var pref in matchedPrefs)
        {
            foreach (var kw in pref.Keywords)
            {
                if (!PassesSingleFilter(item, kw)) return false;
            }
        }
        return true;
    }

    private static bool PassesSingleFilter(StorageItem item, string kw)
    {
        if (kw.StartsWith("MinRarity:", StringComparison.Ordinal))
        {
            var required = kw["MinRarity:".Length..];
            return RarityRank(item.Rarity) >= RarityRank(required);
        }

        if (kw.StartsWith("Rarity:", StringComparison.Ordinal))
        {
            var required = kw["Rarity:".Length..];
            if (required == "Common")
                return item.Rarity is null;
            return string.Equals(item.Rarity, required, StringComparison.OrdinalIgnoreCase);
        }

        if (kw.StartsWith("MinValue:", StringComparison.Ordinal))
        {
            if (int.TryParse(kw.AsSpan("MinValue:".Length), out var minVal))
                return item.Value >= minVal;
        }

        if (kw == "Crafted:y")
            return item.IsCrafted;

        return true;
    }

    private static int RarityRank(string? rarity) => rarity switch
    {
        null => 0,           // Common (no rarity field)
        "Uncommon" => 1,
        "Rare" => 2,
        "Exceptional" => 3,
        "Epic" => 4,
        "Legendary" => 5,
        _ => 0,
    };
}
