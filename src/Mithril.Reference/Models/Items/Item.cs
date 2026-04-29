using System.Collections.Generic;

namespace Mithril.Reference.Models.Items;

/// <summary>
/// One item entry from <c>items.json</c>. Property names match the JSON
/// exactly. <see cref="EffectDescs"/> holds a mix of procedural placeholder
/// strings (e.g. <c>"{MAX_ARMOR}{49}"</c>) and human-readable prose
/// ("Equipping this armor teaches you…"); resolve placeholders via
/// <c>attributes.json</c> at consumption time.
/// </summary>
public sealed class Item
{
    // ─── Always-present fields (per the bundled JSON: 10730/10730 entries) ──
    public string? Description { get; set; }
    public int IconId { get; set; }
    public string? InternalName { get; set; }
    public int MaxStackSize { get; set; }
    public string? Name { get; set; }

    /// <summary>Vendor sale value. Int in 10624 entries, float in 106; modelled as double for tolerance.</summary>
    public double Value { get; set; }

    public IReadOnlyList<string>? Keywords { get; set; }
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
