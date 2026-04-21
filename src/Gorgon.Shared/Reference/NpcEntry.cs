namespace Gorgon.Shared.Reference;

/// <summary>
/// Slim projection of one NPC entry from npcs.json.
/// Contains gift preferences used for favor calculation.
/// </summary>
public sealed record NpcEntry(
    string Key,
    string Name,
    string Area,
    IReadOnlyList<NpcPreference> Preferences,
    IReadOnlyList<string> ItemGiftTiers);

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
