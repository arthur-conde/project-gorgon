namespace Gandalf.Domain;

/// <summary>
/// One entry in the calibration-driven defeat catalog (eventually shipped from
/// <c>mithril-calibration/defeats.json</c>). Reward-cooldown creatures don't
/// emit duration text on the kill itself — the wiki/community is the source
/// of truth, so the duration is per-NPC config.
///
/// All known reward-cooldown creatures share one signal mechanism: the
/// successful corpse-search line as the positive "kill counted" anchor and
/// the <c>"You have already killed &lt;Name&gt; too recently."</c> screen
/// text as the in-cooldown rejection. See the wiki section
/// "Defeat-cooldown creatures" for captured Megaspider + Olugax samples.
/// </summary>
public sealed record DefeatCatalogEntry(
    string Area,
    string NpcInternalName,
    string DisplayName,
    TimeSpan RewardCooldown);
