using System.Text.Json.Serialization;

namespace Gandalf.Domain;

/// <summary>
/// Persisted Loot-source catalog — chest cooldowns observed from the game's
/// re-loot rejection text, plus defeat bosses auto-discovered from the
/// CombatInfo wisdom-credit line. Lives at
/// <c>%LocalAppData%/Mithril/Gandalf/gandalf-loot-catalog.json</c>.
///
/// Both surfaces are observation-driven — no hand-curated seed. Chest
/// durations come from the game's rejection screen; defeat durations come
/// from the community calibration overlay (<c>aggregated/gandalf.json</c>),
/// with a folklore placeholder for not-yet-calibrated bosses.
///
/// Global, not per-character: the durations are properties of the chest /
/// boss template, not the player's interaction with it.
/// </summary>
public sealed class LootCatalogCache
{
    public const int Version = 1;

    public int SchemaVersion { get; set; } = Version;

    /// <summary>
    /// Verified chest cooldown durations learned from the game's re-loot
    /// rejection screen text. Keyed by chest internal name (e.g.
    /// <c>GoblinStaticChest1</c>). Membership here means the duration is
    /// authoritative; rows in <see cref="LearnedChests"/> without a cache entry
    /// fall back to <see cref="Services.LootSource.PlaceholderChestDuration"/>
    /// and surface as <c>IsDurationVerified=false</c>.
    /// </summary>
    public Dictionary<string, TimeSpan> ChestDurationByInternalName { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Chests the player has personally looted in any session. Discovered via
    /// the bracket-tracker AddItem signal regardless of whether the duration is
    /// known yet. Persisted so a chest's row reappears after restart even when
    /// the cooldown is still anchored on a prior session's loot.
    /// </summary>
    public Dictionary<string, LearnedChest> LearnedChests { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Bosses the player has personally killed (observed via the wisdom-credit
    /// line). Keyed by the post-article-strip display name (<c>"Mega-Spider"</c>,
    /// <c>"Olugax the Ever-Pudding"</c>, …) — the same key calibration entries
    /// use. Persisted so the Loot tab shows known bosses across sessions even
    /// before the next kill.
    /// </summary>
    public Dictionary<string, LearnedDefeat> LearnedDefeats { get; set; } =
        new(StringComparer.Ordinal);
}

/// <summary>
/// Per-chest bookkeeping for the auto-discovered catalog. Mirrors
/// <see cref="LearnedDefeat"/>; the chest's duration lives in
/// <see cref="LootCatalogCache.ChestDurationByInternalName"/> only once a
/// rejection has confirmed it.
/// </summary>
public sealed class LearnedChest
{
    public DateTime FirstObservedAt { get; set; }
    public DateTime LastObservedAt { get; set; }
}

/// <summary>
/// Per-boss bookkeeping for the auto-discovered catalog. Sample counts and
/// timestamps are intentionally limited — durations come from calibration,
/// not local observation, so we don't try to derive them from kill deltas.
/// </summary>
public sealed class LearnedDefeat
{
    public DateTime FirstObservedAt { get; set; }
    public DateTime LastObservedAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LootCatalogCache))]
public partial class LootCatalogCacheJsonContext : JsonSerializerContext { }
