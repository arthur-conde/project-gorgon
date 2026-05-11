using System.Collections.Generic;

namespace Mithril.Reference.Models.Items;

/// <summary>
/// One item entry from <c>items.json</c>. Property names match the JSON
/// exactly except for two deliberate deviations (<see cref="Keywords"/>,
/// <see cref="Value"/>) and one lift (<see cref="Id"/>) documented in
/// <c>docs/mithril-reference-shape-quirks.md</c>. <see cref="EffectDescs"/>
/// holds a mix of procedural placeholder strings (e.g. <c>"{MAX_ARMOR}{49}"</c>)
/// and human-readable prose ("Equipping this armor teaches you…"); resolve
/// placeholders via <c>attributes.json</c> at consumption time.
/// </summary>
public sealed class Item
{
    // ─── Always-present fields (per the bundled JSON: 10730/10730 entries) ──

    /// <summary>
    /// Numeric item id. Lifted from the JSON envelope key (<c>"item_5010"</c> →
    /// <c>5010</c>) by the deserializer — the value isn't present on the JSON
    /// entry itself. Lookup tables key on this. See design notebook for context.
    /// </summary>
    public long Id { get; set; }

    public string? Description { get; set; }
    public int IconId { get; set; }
    public string? InternalName { get; set; }
    public int MaxStackSize { get; set; }
    public string? Name { get; set; }

    /// <summary>
    /// Vendor sale value. JSON ships int for 10624 entries and float for 106;
    /// modelled as <see cref="decimal"/> for monetary correctness. See design
    /// notebook for the deviation from "JSON shape exactly".
    /// </summary>
    public decimal Value { get; set; }

    /// <summary>
    /// Parsed keywords. JSON ships these as a flat list of strings shaped like
    /// <c>"VegetarianDish=84"</c> or just <c>"Loot"</c>; the deserializer splits
    /// each entry into an <see cref="ItemKeyword"/>. See design notebook for the
    /// deviation from "JSON shape exactly".
    /// </summary>
    public IReadOnlyList<ItemKeyword>? Keywords { get; set; }
    public IReadOnlyList<ItemBehavior>? Behaviors { get; set; }
    public int? NumUses { get; set; }
    public IReadOnlyDictionary<string, int>? SkillReqs { get; set; }

    /// <summary>Procedural and prose effect strings; placeholders resolve via attributes.json.</summary>
    public IReadOnlyList<string>? EffectDescs { get; set; }

    public string? DroppedAppearance { get; set; }
    public string? EquipSlot { get; set; }
    public int? CraftPoints { get; set; }

    /// <summary>Gear level used to bracket eligible power tiers when rolling augments on this template.</summary>
    public int? CraftingTargetLevel { get; set; }

    /// <summary>Random-roll pool key into <c>tsysprofiles.json</c> (e.g. <c>"All"</c>, <c>"Sword"</c>).</summary>
    public string? TSysProfile { get; set; }

    public string? EquipAppearance { get; set; }
    public string? StockDye { get; set; }
    public string? EquipAppearance2 { get; set; }
    public bool? IsSkillReqsDefaults { get; set; }
    public string? BestowQuest { get; set; }

    /// <summary>Lint-only: vendor NPC who sells this item (recorded for upstream data validation).</summary>
    public string? Lint_VendorNpc { get; set; }

    public bool? AllowPrefix { get; set; }
    public bool? IsCrafted { get; set; }
    public IReadOnlyList<string>? BestowRecipes { get; set; }
    public string? FoodDesc { get; set; }
    public int? BestowTitle { get; set; }
    public string? BestowAbility { get; set; }
    public bool? AllowSuffix { get; set; }
    public int? MaxCarryable { get; set; }
    public string? RequiredAppearance { get; set; }
    public string? MacGuffinQuestName { get; set; }
    public string? DynamicCraftingSummary { get; set; }
    public int? MaxOnVendor { get; set; }
    public string? ColorName { get; set; }
    public string? DyeColor { get; set; }
    public bool? AllowInstallInGuildHalls { get; set; }
    public bool? AllowInstallInHomes { get; set; }
    public string? MountedAppearance { get; set; }
    public bool? AttuneOnPickup { get; set; }
    public bool? DestroyWhenUsedUp { get; set; }
    public string? DroppedAppearanceLookup { get; set; }
    public int? BestowLoreBook { get; set; }
    public bool? IgnoreAlreadyKnownBestowals { get; set; }
    public bool? IsTemporary { get; set; }
    public int? SelfDestructDuration_Hours { get; set; }
}
