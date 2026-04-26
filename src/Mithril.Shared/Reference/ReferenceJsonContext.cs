using System.Text.Json.Serialization;

namespace Mithril.Shared.Reference;

/// <summary>Per-file metadata sidecar (e.g. <c>items.meta.json</c>).</summary>
public sealed class ReferenceFileMetadata
{
    public string CdnVersion { get; set; } = "";
    public DateTimeOffset? FetchedAtUtc { get; set; }
    public ReferenceFileSource Source { get; set; }
}

/// <summary>Raw items.json shape — the fields we actually project into <see cref="ItemEntry"/>.</summary>
public sealed class RawItem
{
    public string? Name { get; set; }
    public string? InternalName { get; set; }
    public int? MaxStackSize { get; set; }
    public int? IconId { get; set; }
    public List<string>? Keywords { get; set; }
    public string? EquipSlot { get; set; }
    public Dictionary<string, int>? SkillReqs { get; set; }
    public decimal? Value { get; set; }
    public string? FoodDesc { get; set; }
    /// <summary>
    /// Procedural effect description strings, e.g. <c>"{MAX_ARMOR}{49}"</c>, <c>"{BOOST_SKILL_WEREWOLF}{12}"</c>.
    /// Human-readable prose entries ("Equipping this armor teaches you…") are mixed in. Resolve placeholder
    /// tokens via <see cref="AttributeEntry"/> (attributes.json).
    /// </summary>
    public List<string>? EffectDescs { get; set; }
    /// <summary>Prose description / flavor text. Present on every item in items.json.</summary>
    public string? Description { get; set; }
    /// <summary>
    /// Random-roll pool key into <c>tsysprofiles.json</c> (e.g. <c>"All"</c>, <c>"Sword"</c>, <c>"MainHandAugment"</c>).
    /// Drives the "Possible augments" preview for both enchanted-template and augment-extractor recipes.
    /// </summary>
    public string? TSysProfile { get; set; }
    /// <summary>
    /// Gear level used to bracket which power tiers are eligible to roll on this template:
    /// a tier rolls when its <c>MinLevel ≤ CraftingTargetLevel ≤ MaxLevel</c>.
    /// </summary>
    public int? CraftingTargetLevel { get; set; }
}

/// <summary>Raw attributes.json shape — resolves placeholder tokens to human-readable labels and formatting hints.</summary>
public sealed class RawAttribute
{
    public string? Label { get; set; }
    public string? DisplayType { get; set; }
    public string? DisplayRule { get; set; }
    public double? DefaultValue { get; set; }
    public List<int>? IconIds { get; set; }
}

/// <summary>Raw recipes.json shape — the fields we project into <see cref="RecipeEntry"/>.</summary>
public sealed class RawRecipe
{
    public string? Name { get; set; }
    public string? InternalName { get; set; }
    public int? IconId { get; set; }
    public string? Skill { get; set; }
    public int? SkillLevelReq { get; set; }
    public string? RewardSkill { get; set; }
    public int? RewardSkillXp { get; set; }
    public int? RewardSkillXpFirstTime { get; set; }
    public int? RewardSkillXpDropOffLevel { get; set; }
    public float? RewardSkillXpDropOffPct { get; set; }
    public int? RewardSkillXpDropOffRate { get; set; }
    public List<RawRecipeItem>? Ingredients { get; set; }
    public List<RawRecipeItem>? ResultItems { get; set; }
    /// <summary>Fallback output list used by crafted-equipment recipes that leave <see cref="ResultItems"/> empty.</summary>
    public List<RawRecipeItem>? ProtoResultItems { get; set; }
    public string? PrereqRecipe { get; set; }
    /// <summary>
    /// Procedural effect strings describing the finished item (e.g.
    /// <c>TSysCraftedEquipment(CraftedWerewolfChest6,0,Werewolf)</c>). Crafted-equipment
    /// recipes encode tier/subtype here rather than in <see cref="ResultItems"/>.
    /// </summary>
    public List<string>? ResultEffects { get; set; }
}

/// <summary>Raw ingredient/result item entry within a recipe.</summary>
public sealed class RawRecipeItem
{
    public long? ItemCode { get; set; }
    public int? StackSize { get; set; }
    public float? ChanceToConsume { get; set; }
    /// <summary>Display label on keyword-matched ingredient slots, e.g. <c>"Auxiliary Crystal"</c>.</summary>
    public string? Desc { get; set; }
    /// <summary>
    /// Keyword set (AND-matched) for ingredient slots that accept any item whose
    /// <see cref="RawItem.Keywords"/> includes every listed tag. Always null on result entries.
    /// </summary>
    public List<string>? ItemKeys { get; set; }
}

/// <summary>Raw skills.json shape — the fields we project into <see cref="SkillEntry"/>.</summary>
public sealed class RawSkill
{
    public int? Id { get; set; }
    public bool? Combat { get; set; }
    public string? XpTable { get; set; }
    public int? MaxBonusLevels { get; set; }
}

/// <summary>Raw xptables.json shape — each table has a name and an array of XP amounts per level.</summary>
public sealed class RawXpTable
{
    public string? InternalName { get; set; }
    public List<long>? XpAmounts { get; set; }
}

/// <summary>Raw npcs.json shape — the fields we project into <see cref="NpcEntry"/>.</summary>
public sealed class RawNpc
{
    public string? Name { get; set; }
    public string? AreaFriendlyName { get; set; }
    public List<RawNpcPreference>? Preferences { get; set; }
    public List<string>? ItemGifts { get; set; }
    public List<RawNpcService>? Services { get; set; }
}

/// <summary>Raw areas.json shape — the fields we project into <see cref="AreaEntry"/>.</summary>
public sealed class RawArea
{
    public string? FriendlyName { get; set; }
    public string? ShortFriendlyName { get; set; }
}

public sealed class RawNpcPreference
{
    public string? Desire { get; set; }
    public List<string>? Keywords { get; set; }
    public string? Name { get; set; }
    public double? Pref { get; set; }
    public string? Favor { get; set; }
}

public sealed class RawNpcService
{
    public string? Type { get; set; }
    public string? Favor { get; set; }
    public List<string>? CapIncreases { get; set; }
}

/// <summary>
/// Raw sources_items.json envelope. The file shape is
/// <c>{ "item_N": { "entries": [ { npc, type, ... }, ... ] } }</c>.
/// </summary>
public sealed class RawItemSourceEnvelope
{
    public List<RawItemSource>? Entries { get; set; }
}

/// <summary>
/// Raw tsysclientinfo.json shape. Each top-level entry is keyed <c>power_NNNN</c> and
/// describes one power that can augment an item. <see cref="Suffix"/> is optional —
/// drop/loot powers carry a display suffix like "of Archery"; deterministic infusion
/// powers (referenced by <c>AddItemTSysPower</c> recipes) typically omit it.
/// </summary>
public sealed class RawPower
{
    public string? InternalName { get; set; }
    public string? Skill { get; set; }
    public List<string>? Slots { get; set; }
    public string? Suffix { get; set; }
    public Dictionary<string, RawPowerTier>? Tiers { get; set; }
}

/// <summary>One tier (<c>id_N</c>) within a <see cref="RawPower"/>.</summary>
public sealed class RawPowerTier
{
    public List<string>? EffectDescs { get; set; }
    /// <summary>Gear level bracket: a tier rolls when <c>MinLevel ≤ CraftingTargetLevel ≤ MaxLevel</c>.</summary>
    public int? MinLevel { get; set; }
    public int? MaxLevel { get; set; }
    /// <summary>Gear rarity gate (e.g. "Uncommon", "Rare", "Epic"); null = any.</summary>
    public string? MinRarity { get; set; }
    /// <summary>Wearer skill level required for the buff to apply (post-roll).</summary>
    public int? SkillLevelPrereq { get; set; }
}

/// <summary>
/// One entry inside <see cref="RawItemSourceEnvelope.Entries"/>. Vendor entries have
/// <c>type: "Vendor"</c> and an <c>npc</c> field; other source types (Recipe, HangOut,
/// NpcGift, Quest, Barter, Monster, Angling, …) use different fields which we surface
/// via <see cref="Recipe"/> / <see cref="Quest"/> / etc.
/// </summary>
public sealed class RawItemSource
{
    public string? Type { get; set; }
    public string? Npc { get; set; }
    public string? Recipe { get; set; }
    public string? Quest { get; set; }
    public string? Monster { get; set; }
    public string? Source { get; set; }
    public string? Interactor { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReferenceFileMetadata))]
[JsonSerializable(typeof(Dictionary<string, RawItem>))]
[JsonSerializable(typeof(Dictionary<string, RawRecipe>))]
[JsonSerializable(typeof(Dictionary<string, RawSkill>))]
[JsonSerializable(typeof(Dictionary<string, RawXpTable>))]
[JsonSerializable(typeof(Dictionary<string, RawNpc>))]
[JsonSerializable(typeof(Dictionary<string, RawArea>))]
[JsonSerializable(typeof(Dictionary<string, RawItemSourceEnvelope>))]
[JsonSerializable(typeof(Dictionary<string, RawAttribute>))]
[JsonSerializable(typeof(Dictionary<string, RawPower>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
public partial class ReferenceJsonContext : JsonSerializerContext { }
