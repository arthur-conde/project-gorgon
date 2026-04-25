using Mithril.Shared.Reference;

namespace Smaug.Domain;

/// <summary>
/// Resolves the effective maximum gold a vendor will pay for a given item, taking
/// into account the player's current favor tier and Civic Pride level. Returns
/// null when the player's favor is below the vendor's <see cref="NpcService.MinFavorTier"/>
/// or when no cap-increase entry covers the item.
/// </summary>
public static class VendorCapResolver
{
    /// <summary>
    /// Civic Pride skill bonus to sell-price cap. Approximate progression per bucket —
    /// tune against observed caps if the wiki numbers prove off.
    /// </summary>
    public static double CivicPrideMultiplierFor(int civicPrideLevel) => civicPrideLevel switch
    {
        <= 4 => 1.00,
        <= 14 => 1.02,
        <= 24 => 1.04,
        <= 34 => 1.06,
        <= 44 => 1.08,
        _ => 1.10,
    };

    /// <summary>
    /// Walks the vendor's CapIncreases up to the player's current favor tier and picks
    /// the highest MaxGold whose keyword filter matches the item (or is empty). Returns
    /// null when the player can't trade with the vendor at all, or no keyword match.
    /// </summary>
    public static int? ResolveMaxGold(
        NpcService store,
        string? playerFavorTier,
        IReadOnlySet<string> itemKeywords,
        int civicPrideLevel)
    {
        if (store.CapIncreases.Count == 0) return null;

        // Gate on MinFavorTier first.
        var currentTier = playerFavorTier ?? FavorTierName.Neutral;
        if (store.MinFavorTier is not null && !FavorTierName.IsAtLeast(currentTier, store.MinFavorTier))
            return null;

        var currentRank = FavorTierName.RankOf(currentTier);
        int? best = null;
        foreach (var cap in store.CapIncreases)
        {
            if (FavorTierName.RankOf(cap.FavorTier) > currentRank) continue;
            if (!MatchesKeywords(cap.Keywords, itemKeywords)) continue;
            if (best is null || cap.MaxGold > best.Value) best = cap.MaxGold;
        }

        if (best is null) return null;
        return (int)Math.Round(best.Value * CivicPrideMultiplierFor(civicPrideLevel));
    }

    /// <summary>
    /// True if the cap-increase entry accepts the item's keywords. An empty keyword
    /// filter matches anything; otherwise any overlap with the item's keywords suffices.
    /// </summary>
    public static bool MatchesKeywords(IReadOnlyList<string> capKeywords, IReadOnlySet<string> itemKeywords)
    {
        if (capKeywords.Count == 0) return true;
        foreach (var k in capKeywords)
            if (itemKeywords.Contains(k)) return true;
        return false;
    }
}
