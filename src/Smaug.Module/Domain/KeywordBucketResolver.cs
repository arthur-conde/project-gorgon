using Mithril.Shared.Reference;

namespace Smaug.Domain;

/// <summary>
/// Chooses a single keyword token from an item's keyword list to use as the
/// ratio-rate key's keyword segment. Priority list mirrors the categories the
/// game itself uses in <c>npcs.json</c> Store <c>CapIncreases</c> entries, so
/// different-instance loot items (augments, gear) still land in the same bucket.
/// </summary>
public static class KeywordBucketResolver
{
    private static readonly string[] PriorityOrder =
    [
        "Armor",
        "Weapon",
        "CorpseTrophy",
        "Augment",
        "Equipment",
        "Food",
        "Consumable",
        "AlchemyIngredient",
        "CookingIngredient",
        "Ingredient",
        "Loot",
    ];

    /// <summary>
    /// Returns the first keyword from <see cref="PriorityOrder"/> present on the item,
    /// else the item's first keyword, else <c>"Uncategorized"</c>.
    /// Ignores virtual keywords synthesized by ReferenceDataService (they contain a colon).
    /// </summary>
    public static string Resolve(ItemEntry item)
    {
        if (item.Keywords.Count == 0) return "Uncategorized";

        var tags = item.Keywords
            .Select(k => k.Tag)
            .Where(t => !string.IsNullOrEmpty(t) && !t.Contains(':'))
            .ToList();

        if (tags.Count == 0) return "Uncategorized";

        foreach (var priority in PriorityOrder)
            if (tags.Contains(priority, StringComparer.Ordinal))
                return priority;

        return tags[0];
    }
}
