using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One power entry from <c>tsysclientinfo.json</c>; keyed by <c>"power_NNNN"</c>.
/// Describes a power that can augment an item, with one or more
/// <see cref="Tiers"/> giving level-bracketed effect rolls.
/// </summary>
public sealed class PowerProfile
{
    public string? InternalName { get; set; }
    public string? Skill { get; set; }
    public IReadOnlyList<string>? Slots { get; set; }

    /// <summary>Map from tier id (e.g. <c>"id_1"</c>) to the tier's roll bracket and effect descriptions.</summary>
    public IReadOnlyDictionary<string, PowerTier>? Tiers { get; set; }

    /// <summary>Display suffix for drop/loot powers (e.g. <c>"of Archery"</c>); null on deterministic infusion powers.</summary>
    public string? Suffix { get; set; }

    public string? Prefix { get; set; }
    public bool? IsUnavailable { get; set; }
    public bool? IsHiddenFromTransmutation { get; set; }
}

/// <summary>One tier within a <see cref="PowerProfile"/>'s <c>Tiers</c> map.</summary>
public sealed class PowerTier
{
    public IReadOnlyList<string>? EffectDescs { get; set; }

    /// <summary>Gear level bracket: a tier rolls when MinLevel ≤ CraftingTargetLevel ≤ MaxLevel.</summary>
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }

    /// <summary>Gear rarity gate (e.g. <c>"Uncommon"</c>, <c>"Rare"</c>).</summary>
    public string? MinRarity { get; set; }

    /// <summary>Wearer skill level required for the buff to apply (post-roll).</summary>
    public int SkillLevelPrereq { get; set; }
}
