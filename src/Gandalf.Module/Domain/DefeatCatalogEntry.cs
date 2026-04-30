namespace Gandalf.Domain;

/// <summary>
/// One entry in the calibration-driven defeat catalog (eventually shipped from
/// <c>mithril-calibration/defeats.json</c>). Reward-cooldown creatures don't
/// emit duration text on the kill itself — the wiki/community is the source
/// of truth, so the duration is per-NPC config.
/// </summary>
public sealed record DefeatCatalogEntry(
    string Area,
    string NpcInternalName,
    string DisplayName,
    TimeSpan RewardCooldown);
