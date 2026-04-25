namespace Mithril.Shared.Reference;

/// <summary>
/// One card in the augment-pool viewer: a single power's tiers collapsed into a
/// header (skill, suffix, tier and gear-level ranges) plus the lowest in-scope
/// tier's effect lines as a "floor" preview. The full per-tier breakdown lives
/// in <see cref="Tiers"/> for the expander body.
/// </summary>
/// <remarks>
/// Built post-filter: only tiers that survived the user's query end up in
/// <see cref="Tiers"/>, so the header ranges and floor preview describe what's
/// actually visible — not the unfiltered power.
/// </remarks>
public sealed record GroupedAugmentOption(
    string PowerInternalName,
    string? Suffix,
    string Skill,
    IReadOnlyList<PooledAugmentOption> Tiers,
    string? ItemName = null)
{
    private PooledAugmentOption FloorTier => Tiers[0];
    private PooledAugmentOption CeilingTier => Tiers[^1];

    public int MinTier => FloorTier.Tier;
    public int MaxTier => CeilingTier.Tier;

    /// <summary>Lowest <c>MinLevel</c> across the in-scope tiers, or null if none expose a min.</summary>
    public int? MinLevel
    {
        get
        {
            int? min = null;
            foreach (var t in Tiers)
            {
                if (t.MinLevel is { } v && (min is null || v < min)) min = v;
            }
            return min;
        }
    }

    /// <summary>Highest <c>MaxLevel</c> across the in-scope tiers, or null if none expose a max.</summary>
    public int? MaxLevel
    {
        get
        {
            int? max = null;
            foreach (var t in Tiers)
            {
                if (t.MaxLevel is { } v && (max is null || v > max)) max = v;
            }
            return max;
        }
    }

    /// <summary>The lowest in-scope tier's rendered effect lines — the floor preview.</summary>
    public IReadOnlyList<EffectLine> FloorEffectLines => FloorTier.EffectLines;

    /// <summary>
    /// Card header. Joins the source-item's display name with the suffix when both are
    /// known (the player-facing in-game name, e.g. "Quality Werewolf Hindguard of Blood
    /// Geysers"); falls back to "{InternalName} ({Suffix})" when only the suffix is known
    /// (e.g. an extraction pool with no contextual item), or just the internal name when
    /// the power has no suffix at all.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (ItemName is { Length: > 0 } item && Suffix is { Length: > 0 } suffix)
                return $"{item} {suffix}";
            if (Suffix is { Length: > 0 } onlySuffix)
                return $"{PowerInternalName} ({onlySuffix})";
            return PowerInternalName;
        }
    }

    /// <summary>"Tier 7-10" or "Tier 7" if min == max.</summary>
    public string TierRange => MinTier == MaxTier ? $"Tier {MinTier}" : $"Tier {MinTier}-{MaxTier}";

    /// <summary>"Lvl 35-65", or "Lvl 35" / "Lvl 35+" / null when bounds are missing or equal.</summary>
    public string? LevelRange
    {
        get
        {
            var min = MinLevel;
            var max = MaxLevel;
            if (min is null && max is null) return null;
            if (min is not null && max is not null)
                return min == max ? $"Lvl {min}" : $"Lvl {min}-{max}";
            if (min is not null) return $"Lvl {min}+";
            return $"Lvl ≤{max}";
        }
    }

    /// <summary>"Tier 7-10 · Lvl 35-65", or just "Tier 7-10" when no level bounds. One-shot card sub-line.</summary>
    public string RangesLine => LevelRange is { } lvl ? $"{TierRange} · {lvl}" : TierRange;
}
