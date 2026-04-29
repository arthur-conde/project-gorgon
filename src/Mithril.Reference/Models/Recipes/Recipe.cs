using System.Collections.Generic;

namespace Mithril.Reference.Models.Recipes;

/// <summary>
/// One recipe entry from <c>recipes.json</c>. Property names match the JSON
/// exactly. <see cref="ResultEffects"/> and <see cref="ResultEffectsThatCanFail"/>
/// hold procedural strings (e.g. <c>"TSysCraftedEquipment(...)"</c>) — parse
/// those at consumption time using <c>Mithril.Shared.Reference.ResultEffectsParser</c>.
/// </summary>
public sealed class Recipe
{
    // ─── Always-present fields (per the bundled JSON: 4427/4427 entries) ──
    public string? Description { get; set; }
    public int IconId { get; set; }
    public IReadOnlyList<RecipeIngredient>? Ingredients { get; set; }
    public string? InternalName { get; set; }
    public string? Name { get; set; }
    public IReadOnlyList<RecipeResultItem>? ResultItems { get; set; }
    public string? RewardSkill { get; set; }
    public int RewardSkillXp { get; set; }
    public int RewardSkillXpFirstTime { get; set; }
    public string? Skill { get; set; }
    public int SkillLevelReq { get; set; }

    // ─── XP drop-off (always paired) ──
    public int? RewardSkillXpDropOffLevel { get; set; }
    public double? RewardSkillXpDropOffPct { get; set; }
    public int? RewardSkillXpDropOffRate { get; set; }

    // ─── Optional fields ──
    public IReadOnlyList<string>? Keywords { get; set; }
    public string? UsageAnimation { get; set; }

    /// <summary>UsageDelay is int in 2913 entries but float in 4 — modelled as double for tolerance.</summary>
    public double? UsageDelay { get; set; }

    public string? UsageDelayMessage { get; set; }
    public string? ActionLabel { get; set; }

    /// <summary>Procedural effect strings; parse with ResultEffectsParser.</summary>
    public IReadOnlyList<string>? ResultEffects { get; set; }

    public string? PrereqRecipe { get; set; }
    public string? UsageAnimationEnd { get; set; }

    /// <summary>Fallback output list used by crafted-equipment recipes that leave ResultItems empty.</summary>
    public IReadOnlyList<RecipeResultItem>? ProtoResultItems { get; set; }

    public string? SortSkill { get; set; }
    public string? LoopParticle { get; set; }
    public string? Particle { get; set; }
    public string? ItemMenuLabel { get; set; }
    public string? ItemMenuKeywordReq { get; set; }
    public bool? IsItemMenuKeywordReqSufficient { get; set; }
    public int? MaxUses { get; set; }

    /// <summary>Dict-or-array in JSON; coerced to a list by SingleOrArrayConverter.</summary>
    public IReadOnlyList<RecipeRequirement>? OtherRequirements { get; set; }

    public string? DyeColor { get; set; }
    public int? ItemMenuCategoryLevel { get; set; }
    public IReadOnlyList<RecipeCost>? Costs { get; set; }
    public string? ItemMenuCategory { get; set; }
    public int? ResetTimeInSeconds { get; set; }
    public string? SharesResetTimerWith { get; set; }
    public IReadOnlyList<string>? ValidationIngredientKeywords { get; set; }
    public bool? RewardAllowBonusXp { get; set; }
    public int? NumResultItems { get; set; }
    public IReadOnlyList<string>? ResultEffectsThatCanFail { get; set; }
    public string? RequiredAttributeNonZero { get; set; }
}
