using System.Collections.Generic;

namespace Mithril.Reference.Models.Misc;

/// <summary>
/// One skill from <c>skills.json</c>; keyed by skill internal name
/// (e.g. <c>"Alchemy"</c>, <c>"Sword"</c>).
/// </summary>
public sealed class Skill
{
    public bool Combat { get; set; }
    public string? Description { get; set; }
    public int GuestLevelCap { get; set; }
    public int Id { get; set; }
    public int MaxBonusLevels { get; set; }
    public string? XpTable { get; set; }

    /// <summary>Map from level (as string, e.g. <c>"10"</c>) to the reward earned at that level.</summary>
    public IReadOnlyDictionary<string, SkillReward>? Rewards { get; set; }

    public string? Name { get; set; }
    public IReadOnlyList<string>? Parents { get; set; }

    /// <summary>Map from level (as string) to a hint string about how to earn more XP at that level.</summary>
    public IReadOnlyDictionary<string, string>? AdvancementHints { get; set; }

    /// <summary>Map from interaction-flag string to the level cap that applies while the flag is set.</summary>
    public IReadOnlyDictionary<string, int>? InteractionFlagLevelCaps { get; set; }

    public IReadOnlyList<string>? XpEarnedAttributes { get; set; }
    public string? ProdigyEnabledInteractionFlag { get; set; }
    public string? PassiveAdvancementTable { get; set; }
    public IReadOnlyList<string>? TSysCompatibleCombatSkills { get; set; }
    public string? ActiveAdvancementTable { get; set; }
    public IReadOnlyList<string>? AssociatedItemKeywords { get; set; }
    public bool? IsFakeCombatSkill { get; set; }
    public bool? AuxCombat { get; set; }

    /// <summary>Map of report metadata (currently always empty in the bundled data; shape unverified).</summary>
    public IReadOnlyDictionary<string, object>? Reports { get; set; }

    public bool? HideWhenZero { get; set; }
    public IReadOnlyList<string>? AssociatedAppearances { get; set; }
    public IReadOnlyList<string>? RecipeIngredientKeywords { get; set; }
    public bool? IsUmbrellaSkill { get; set; }
    public IReadOnlyList<string>? DisallowedItemKeywords { get; set; }
    public bool? SkipBonusLevelsIfSkillUnlearned { get; set; }
    public bool? SkillLevelDisparityApplies { get; set; }

    /// <summary>Underscore-prefixed field appears once; treat as data-author note.</summary>
    public IReadOnlyList<string>? _RecipeIngredientKeywords { get; set; }

    public IReadOnlyList<string>? DisallowedAppearances { get; set; }
}

/// <summary>
/// One per-level reward inside <see cref="Skill.Rewards"/>. At least one of
/// the fields is set per row. <see cref="Ability"/> is mostly a single
/// string but ships as an array in 1 outlier — modelled as a list for
/// faithfulness.
/// </summary>
public sealed class SkillReward
{
    public string? BonusToSkill { get; set; }

    /// <summary>String-or-array in JSON; the array path coerces via SingleOrArrayConverter.</summary>
    public IReadOnlyList<string>? Ability { get; set; }

    public string? Notes { get; set; }
    public string? Recipe { get; set; }
}
