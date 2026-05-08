namespace Mithril.Shared.Reference;

/// <summary>
/// Slim projection of one entry in skills.json. <see cref="Key"/> is the
/// id-shaped dictionary key (e.g. <c>"AncillaryArmorAugmentBrewing"</c>) and
/// matches recipes' <c>RewardSkill</c> field; it is guaranteed to be ASCII
/// identifier-safe (matches <c>[A-Za-z0-9_]+</c>) and is the canonical id used
/// for settings persistence and <c>mithril://elrond/{key}</c> deep links.
/// <see cref="DisplayName"/> is the human-readable in-game name (e.g.
/// <c>"Ancillary-Armor Augmentation"</c>) sourced from the JSON <c>Name</c>
/// field; it may contain spaces and hyphens.
/// </summary>
public sealed record SkillEntry(
    string Key,
    string DisplayName,
    int Id,
    bool Combat,
    string XpTable,
    int MaxBonusLevels,
    IReadOnlyList<string> Parents,
    IReadOnlyDictionary<string, SkillRewardEntry> Rewards);
