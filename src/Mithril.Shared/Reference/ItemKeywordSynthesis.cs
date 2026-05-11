using Mithril.Reference.Models.Items;

namespace Mithril.Shared.Reference;

/// <summary>
/// Mithril-side enrichment of an item's raw <c>Keywords</c> with virtual
/// matching tags. NPC gift preferences in <c>npcs.json</c> reference filter
/// keywords like <c>SkillPrereq:Archery</c>, <c>EquipmentSlot:Head</c>,
/// <c>MinRarity:Rare</c>, <c>MinValue:1000</c> that don't appear on the items
/// themselves; recipe <c>ItemKeys</c> slots reference virtual
/// <c>EquipmentSlot:</c> tags too. Synthesizing these from the item's other
/// fields keeps the matching engine simple at the cost of a per-item enrichment
/// pass at reference-data load.
/// </summary>
/// <remarks>
/// This is matching machinery, not part of the items.json shape. It lives in
/// <c>Mithril.Shared.Reference</c> rather than on <c>Mithril.Reference.Item</c>
/// so the POCO layer stays a faithful projection of the JSON.
/// </remarks>
public static class ItemKeywordSynthesis
{
    /// <summary>
    /// Enumerate the union of an item's raw keywords and the virtual matching
    /// tags synthesised from <see cref="Item.EquipSlot"/>, <see cref="Item.SkillReqs"/>,
    /// <see cref="Item.Value"/>, and a small rule applied to <c>Loot</c> /
    /// <c>Equipment</c> items. Virtual tags always have <c>Quality = 0</c>.
    /// </summary>
    public static IEnumerable<ItemKeyword> Enrich(Item item)
    {
        var raw = item.Keywords;
        if (raw is not null)
        {
            foreach (var kw in raw) yield return kw;
        }

        // Synthesize "EquipmentSlot:{slot}" virtual keyword
        if (!string.IsNullOrEmpty(item.EquipSlot))
            yield return new ItemKeyword($"EquipmentSlot:{item.EquipSlot}", 0);

        // Synthesize "SkillPrereq:{skill}" virtual keywords (one per skill req)
        if (item.SkillReqs is not null)
        {
            foreach (var skill in item.SkillReqs.Keys)
                yield return new ItemKeyword($"SkillPrereq:{skill}", 0);
        }

        // Synthesize "MinValue:{threshold}" virtual keywords for common thresholds.
        // Cast via int to mirror the slim path's behaviour verbatim.
        var v = (int)item.Value;
        if (v >= 1000) yield return new ItemKeyword("MinValue:1000", 0);
        if (v >= 500) yield return new ItemKeyword("MinValue:500", 0);

        // Synthesize rarity virtual keywords from the keyword list.
        // "Loot" items can drop at any rarity; "Stock" items are always Common.
        var hasLoot = false;
        var hasEquipment = false;
        if (raw is not null)
        {
            foreach (var kw in raw)
            {
                if (kw.Tag == "Loot") hasLoot = true;
                else if (kw.Tag == "Equipment") hasEquipment = true;
            }
        }
        if (hasLoot)
        {
            // Loot items can be any rarity — match all MinRarity filters
            yield return new ItemKeyword("MinRarity:Uncommon", 0);
            yield return new ItemKeyword("MinRarity:Rare", 0);
            yield return new ItemKeyword("MinRarity:Epic", 0);
        }
        else if (hasEquipment)
        {
            yield return new ItemKeyword("Rarity:Common", 0);
        }
    }
}
