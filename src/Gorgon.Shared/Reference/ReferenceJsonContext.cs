using System.Text.Json.Serialization;

namespace Gorgon.Shared.Reference;

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
    public string? PrereqRecipe { get; set; }
}

/// <summary>Raw ingredient/result item entry within a recipe.</summary>
public sealed class RawRecipeItem
{
    public long? ItemCode { get; set; }
    public int? StackSize { get; set; }
    public float? ChanceToConsume { get; set; }
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
[JsonSerializable(typeof(Dictionary<string, RawItemSourceEnvelope>))]
public partial class ReferenceJsonContext : JsonSerializerContext { }
