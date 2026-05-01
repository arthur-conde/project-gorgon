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

    /// <summary>Map keyed by chest internal name (e.g. <c>GoblinStaticChest1</c>).</summary>
    public Dictionary<string, TimeSpan> ChestDurationByInternalName { get; set; } =
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
