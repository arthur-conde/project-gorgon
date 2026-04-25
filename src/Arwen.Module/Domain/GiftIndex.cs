using Mithril.Shared.Reference;

namespace Arwen.Domain;

/// <summary>
/// A matched gift: an item that matches an NPC preference keyword.
/// Exact favor-per-gift cannot be calculated from CDN data — the game uses
/// unpublished per-category rates. <see cref="RelativeScore"/> provides a
/// ranking metric (pref × itemValue) for comparing items within the same NPC.
/// </summary>
public sealed record GiftMatch(
    long ItemId,
    string ItemName,
    int IconId,
    string Desire,
    double Pref,
    double ItemValue,
    double RelativeScore,
    string? UnlockTier,
    string MatchedKeyword);

/// <summary>
/// Precomputed index mapping NPC preferences to matching items.
/// Built once from reference data; rebuilt atomically when CDN data refreshes.
/// </summary>
public sealed class GiftIndex
{
    private IReadOnlyDictionary<string, IReadOnlyList<GiftMatch>> _npcGifts =
        new Dictionary<string, IReadOnlyList<GiftMatch>>(StringComparer.Ordinal);

    private IReadOnlyDictionary<long, ItemEntry> _items =
        new Dictionary<long, ItemEntry>();

    // Per-NPC, per-item: all preferences that matched (not just the best).
    // Used by calibration to record multi-preference matches for rate modeling.
    private IReadOnlyDictionary<string, IReadOnlyDictionary<long, IReadOnlyList<MatchedPreference>>> _allMatchesByNpcItem =
        new Dictionary<string, IReadOnlyDictionary<long, IReadOnlyList<MatchedPreference>>>(StringComparer.Ordinal);

    public event EventHandler? Rebuilt;

    public void Build(IReadOnlyDictionary<long, ItemEntry> items, IReadOnlyDictionary<string, NpcEntry> npcs)
    {
        // Step 1: Build per-item keyword tag set and quality lookup for fast matching
        var itemKeywordSets = new Dictionary<long, HashSet<string>>(items.Count);
        var itemKeywordQuality = new Dictionary<long, Dictionary<string, int>>(items.Count);
        foreach (var (id, item) in items)
        {
            var tags = new HashSet<string>(item.Keywords.Count, StringComparer.Ordinal);
            var quals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kw in item.Keywords)
            {
                tags.Add(kw.Tag);
                if (kw.Quality > 0 && (!quals.TryGetValue(kw.Tag, out var existing) || kw.Quality > existing))
                    quals[kw.Tag] = kw.Quality;
            }
            itemKeywordSets[id] = tags;
            if (quals.Count > 0) itemKeywordQuality[id] = quals;
        }

        // Step 2: For each NPC preference, find items matching ALL keywords (AND logic)
        var npcGifts = new Dictionary<string, IReadOnlyList<GiftMatch>>(npcs.Count, StringComparer.Ordinal);
        var allMatchesByNpcItem = new Dictionary<string, IReadOnlyDictionary<long, IReadOnlyList<MatchedPreference>>>(npcs.Count, StringComparer.Ordinal);
        foreach (var (npcKey, npc) in npcs)
        {
            var bestPerItem = new Dictionary<long, GiftMatch>();
            var allPrefsPerItem = new Dictionary<long, List<MatchedPreference>>();

            foreach (var pref in npc.Preferences)
            {
                if (pref.Keywords.Count == 0) continue;

                foreach (var (itemId, item) in items)
                {
                    if (!itemKeywordSets.TryGetValue(itemId, out var itemTags)) continue;

                    // Check ALL preference keywords are present on this item
                    var allMatch = true;
                    foreach (var kwTag in pref.Keywords)
                    {
                        if (!itemTags.Contains(kwTag)) { allMatch = false; break; }
                    }
                    if (!allMatch) continue;

                    var matchedKw = pref.Keywords[0];
                    var itemValue = (double)item.Value;
                    var relativeScore = pref.Pref * Math.Max(itemValue, 1);

                    if (!bestPerItem.TryGetValue(itemId, out var existing) || relativeScore > existing.RelativeScore)
                    {
                        bestPerItem[itemId] = new GiftMatch(
                            itemId, item.Name, item.IconId, pref.Desire, pref.Pref, itemValue, relativeScore,
                            pref.RequiredFavorTier, matchedKw);
                    }

                    // Also record this preference on the item's full match list
                    if (!allPrefsPerItem.TryGetValue(itemId, out var list))
                    {
                        list = new List<MatchedPreference>();
                        allPrefsPerItem[itemId] = list;
                    }
                    list.Add(new MatchedPreference
                    {
                        Name = pref.Name,
                        Desire = pref.Desire,
                        Pref = pref.Pref,
                        Keywords = [.. pref.Keywords],
                    });
                }
            }

            var matches = bestPerItem.Values.ToList();
            matches.Sort((a, b) => b.RelativeScore.CompareTo(a.RelativeScore));
            npcGifts[npcKey] = matches;

            var allMatches = new Dictionary<long, IReadOnlyList<MatchedPreference>>(allPrefsPerItem.Count);
            foreach (var (itemId, list) in allPrefsPerItem)
                allMatches[itemId] = list;
            allMatchesByNpcItem[npcKey] = allMatches;
        }

        _npcGifts = npcGifts;
        _items = items;
        _allMatchesByNpcItem = allMatchesByNpcItem;
        Rebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>All items matching an NPC's preferences, sorted by favor descending.</summary>
    public IReadOnlyList<GiftMatch> GetGiftsForNpc(string npcKey) =>
        _npcGifts.TryGetValue(npcKey, out var list) ? list : [];

    /// <summary>Match a specific item against a specific NPC's preferences.</summary>
    public GiftMatch? MatchItemToNpc(long itemId, string npcKey)
    {
        var gifts = GetGiftsForNpc(npcKey);
        foreach (var g in gifts)
            if (g.ItemId == itemId)
                return g;
        return null;
    }

    /// <summary>
    /// All NPC preferences the given item satisfies. An item may match multiple preferences
    /// (e.g. "Ring" + "MinRarity:Rare"), and the game's favor-per-gift rate appears to depend
    /// on the full set, not just the highest-scoring one.
    /// </summary>
    public IReadOnlyList<MatchedPreference> MatchAllPreferencesForItem(long itemId, string npcKey)
    {
        if (!_allMatchesByNpcItem.TryGetValue(npcKey, out var perItem)) return [];
        return perItem.TryGetValue(itemId, out var list) ? list : [];
    }

    /// <summary>All NPCs who want a specific item, sorted by pref descending.</summary>
    public IReadOnlyList<NpcGiftMatch> GetNpcsForItem(long itemId)
    {
        var results = new List<NpcGiftMatch>();
        foreach (var (npcKey, gifts) in _npcGifts)
        {
            foreach (var g in gifts)
            {
                if (g.ItemId != itemId) continue;
                results.Add(new NpcGiftMatch(npcKey, g));
                break; // one match per NPC
            }
        }
        results.Sort((a, b) => b.Match.Pref.CompareTo(a.Match.Pref));
        return results;
    }

    /// <summary>Look up item entry by ID.</summary>
    public ItemEntry? GetItem(long itemId) =>
        _items.TryGetValue(itemId, out var entry) ? entry : null;
}

/// <summary>An NPC that wants a specific item.</summary>
public sealed record NpcGiftMatch(string NpcKey, GiftMatch Match);
