namespace Gandalf.Domain;

/// <summary>
/// One entry in the community calibration overlay applied to <see cref="Services.LootSource"/>
/// (sourced from <c>mithril-calibration/aggregated/gandalf.json</c>). Reward-cooldown
/// creatures don't emit their cooldown duration in any log line — the wiki/community is the
/// source of truth, so the duration is per-NPC config.
///
/// <see cref="DisplayName"/> matches the post-article-strip wisdom-line form
/// (<c>"Mega-Spider"</c>, <c>"Olugax the Ever-Pudding"</c>, <c>"Den Mother"</c>, …) — the
/// same key the auto-discovery path uses, so calibration entries map cleanly onto
/// learned bosses.
///
/// All known reward-cooldown creatures share one signal mechanism: the wisdom-credit line
/// as the positive "kill counted" anchor and the <c>"You have already killed &lt;Name&gt;
/// too recently."</c> screen text as the in-cooldown rejection. See the wiki section
/// "Defeat-cooldown creatures" for captured Megaspider + Olugax samples.
/// </summary>
public sealed record DefeatCatalogEntry(
    string DisplayName,
    TimeSpan RewardCooldown,
    string? Area = null);
