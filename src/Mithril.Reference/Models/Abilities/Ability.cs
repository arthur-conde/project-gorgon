using System.Collections.Generic;

namespace Mithril.Reference.Models.Abilities;

/// <summary>
/// One ability entry from <c>abilities.json</c>; keyed by <c>"ability_NNNN"</c>.
/// Property names match JSON exactly. The nested <see cref="PvE"/> block holds
/// the per-version stat data; top-level fields describe ability metadata
/// (animations, prerequisites, ammo, sidebar visibility, etc.).
/// </summary>
public sealed class Ability
{
    // Always-present fields (per the bundled data: 5894/5894).
    public string? Animation { get; set; }
    public string? DamageType { get; set; }
    public string? Description { get; set; }
    public int IconID { get; set; }
    public string? InternalName { get; set; }
    public int Level { get; set; }
    public string? Name { get; set; }
    public AbilityPvE? PvE { get; set; }

    /// <summary>Float in 326 entries, int in 5568; modelled as double.</summary>
    public double ResetTime { get; set; }

    public string? Skill { get; set; }
    public string? Target { get; set; }

    // Optional fields (most → least common).
    public IReadOnlyList<string>? AttributesThatDeltaResetTime { get; set; }
    public IReadOnlyList<string>? Keywords { get; set; }
    public IReadOnlyList<string>? CausesOfDeath { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaPowerCost { get; set; }
    public IReadOnlyList<string>? AttributesThatModPowerCost { get; set; }
    public string? SharesResetTimerWith { get; set; }
    public string? UpgradeOf { get; set; }
    public string? Prerequisite { get; set; }
    public string? TargetParticle { get; set; }
    public IReadOnlyList<string>? ItemKeywordReqs { get; set; }
    public string? ItemKeywordReqErrorMessage { get; set; }
    public string? SpecialInfo { get; set; }
    public bool? IsHarmless { get; set; }
    public bool? WorksUnderwater { get; set; }
    public string? Projectile { get; set; }
    public bool? WorksWhileFalling { get; set; }
    public IReadOnlyList<AbilityConditionalKeyword>? ConditionalKeywords { get; set; }
    public string? AmmoDescription { get; set; }
    public IReadOnlyList<AbilityAmmoKeyword>? AmmoKeywords { get; set; }

    /// <summary>Dict-or-array in JSON; coerced to a list by SingleOrArrayConverter.</summary>
    public IReadOnlyList<AbilitySpecialCasterRequirement>? SpecialCasterRequirements { get; set; }

    public string? SelfParticle { get; set; }
    public bool? CanBeOnSidebar { get; set; }
    public int? CombatRefreshBaseAmount { get; set; }
    public string? SpecialCasterRequirementsErrorMessage { get; set; }
    public string? SelfPreParticle { get; set; }
    public bool? CanSuppressMonsterShout { get; set; }

    /// <summary>Float in 171 entries, int in 96; modelled as double.</summary>
    public double? AmmoStickChance { get; set; }

    public bool? InternalAbility { get; set; }
    public string? PetTypeTagReq { get; set; }
    public int? PetTypeTagReqMax { get; set; }
    public string? AbilityGroup { get; set; }
    public int? DelayLoopTime { get; set; }
    public string? DelayLoopMessage { get; set; }
    public string? AbilityGroupName { get; set; }
    public bool? WorksInCombat { get; set; }
    public bool? DelayLoopIsAbortedIfAttacked { get; set; }
    public string? Rank { get; set; }
    public IReadOnlyList<string>? EffectKeywordsIndicatingEnabled { get; set; }

    /// <summary>Float in 89 entries, int in 6; modelled as double.</summary>
    public double? AmmoConsumeChance { get; set; }

    public IReadOnlyList<string>? AttributesThatDeltaDelayLoopTime { get; set; }
    public IReadOnlyList<string>? ExtraKeywordsForTooltips { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaWorksWhileStunned { get; set; }
    public bool? WorksWhileStunned { get; set; }
    public bool? IgnoreEffectErrors { get; set; }
    public IReadOnlyList<string>? AttributesThatDeltaCritChance { get; set; }
    public string? AttributeThatPreventsDelayLoopAbortOnAttacked { get; set; }
    public bool? IsCosmeticPet { get; set; }
    public bool? DelayLoopIsOnlyUsedInCombat { get; set; }
    public int? SpecialTargetingTypeReq { get; set; }
    public bool? AoEIsCenteredOnCaster { get; set; }
    public IReadOnlyList<string>? AttributesThatModAmmoConsumeChance { get; set; }
    public bool? IsTimerResetWhenDisabling { get; set; }
    public bool? WorksWhileMounted { get; set; }
    public string? TargetEffectKeywordReq { get; set; }
    public string? InventoryKeywordReqErrorMessage { get; set; }
    public IReadOnlyList<string>? InventoryKeywordReqs { get; set; }
    public bool? CanTargetUntargetableEnemies { get; set; }
    public IReadOnlyList<AbilityCost>? Costs { get; set; }
    public string? EffectKeywordReqErrorMessage { get; set; }
    public IReadOnlyList<string>? EffectKeywordReqs { get; set; }
    public string? TargetTypeTagReq { get; set; }
}
