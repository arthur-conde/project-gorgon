namespace Gorgon.Shared.Reference;

/// <summary>
/// Slim projection of one NPC entry from npcs.json.
/// Contains gift preferences used for favor calculation, plus
/// service metadata (Store/Training/etc.) used by the Smaug vendor module.
/// </summary>
public sealed record NpcEntry(
    string Key,
    string Name,
    string Area,
    IReadOnlyList<NpcPreference> Preferences,
    IReadOnlyList<string> ItemGiftTiers,
    IReadOnlyList<NpcService> Services);

/// <summary>
/// A single gift preference for an NPC. NPCs love/like/hate items
/// matching certain keyword tags. <see cref="Pref"/> is the multiplier
/// applied to an item's keyword quality to compute favor-per-gift.
/// </summary>
public sealed record NpcPreference(
    string Desire,
    IReadOnlyList<string> Keywords,
    string Name,
    double Pref,
    string? RequiredFavorTier);

/// <summary>
/// A service an NPC offers (Store, Training, Barter, …). Only Store services carry
/// <see cref="CapIncreases"/> — the per-favor-tier gold caps the vendor will pay
/// for items in listed keyword categories.
/// </summary>
public sealed record NpcService(
    string Type,
    string? MinFavorTier,
    IReadOnlyList<NpcStoreCapIncrease> CapIncreases);

/// <summary>
/// One entry of a Store service's <c>CapIncreases</c> array. Parsed from the
/// raw string form <c>"&lt;FavorTier&gt;:&lt;MaxGold&gt;:&lt;keyword,keyword,…&gt;"</c>.
/// An empty <see cref="Keywords"/> list means the cap applies to any item.
/// </summary>
public sealed record NpcStoreCapIncrease(
    string FavorTier,
    int MaxGold,
    IReadOnlyList<string> Keywords);
