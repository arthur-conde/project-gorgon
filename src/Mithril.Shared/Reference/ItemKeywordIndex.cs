using Mithril.Reference.Models.Items;

namespace Mithril.Shared.Reference;

/// <summary>
/// Catalog-side inverted index: keyword tag → items in <c>items.json</c> carrying that tag.
/// Built once when items reload; powers keyword-matched ingredient resolution
/// (every <c>*E</c> enchanted recipe carries a slot like <c>{ "ItemKeys": ["Crystal"] }</c>
/// that means "any item whose <see cref="Item.Keywords"/> includes Crystal").
/// Multi-key sets are resolved as AND — every listed tag must be present.
/// </summary>
/// <remarks>
/// Index entries include both the raw <see cref="Item.Keywords"/> tags and the
/// virtual matching tags produced by <see cref="ItemKeywordSynthesis.Enrich"/>
/// (e.g. <c>EquipmentSlot:MainHand</c>, <c>SkillPrereq:Archery</c>), so recipe
/// <c>ItemKeys</c> slots that reference virtual tags resolve correctly.
/// </remarks>
public sealed class ItemKeywordIndex
{
    public static readonly ItemKeywordIndex Empty = new(new Dictionary<long, Item>());

    private readonly IReadOnlyDictionary<string, IReadOnlyList<Item>> _byKeyword;

    public ItemKeywordIndex(IReadOnlyDictionary<long, Item> items)
    {
        var map = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        foreach (var item in items.Values)
        {
            foreach (var kw in ItemKeywordSynthesis.Enrich(item))
            {
                if (!map.TryGetValue(kw.Tag, out var bucket))
                    map[kw.Tag] = bucket = [];
                bucket.Add(item);
            }
        }
        _byKeyword = map.ToDictionary(p => p.Key, p => (IReadOnlyList<Item>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>Items carrying the given single tag (empty if unknown).</summary>
    public IReadOnlyList<Item> ByKeyword(string tag) =>
        _byKeyword.TryGetValue(tag, out var list) ? list : [];

    /// <summary>Items carrying every tag in <paramref name="keys"/> (AND-match).</summary>
    public IReadOnlyList<Item> ItemsMatching(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return [];
        if (keys.Count == 1) return ByKeyword(keys[0]);

        var seed = new HashSet<Item>(ByKeyword(keys[0]));
        for (var i = 1; i < keys.Count && seed.Count > 0; i++)
            seed.IntersectWith(ByKeyword(keys[i]));
        return [.. seed];
    }

    /// <summary>Display string for a keyword set, joined with " + ".</summary>
    public static string Humanise(IReadOnlyList<string> keys) => string.Join(" + ", keys);
}
