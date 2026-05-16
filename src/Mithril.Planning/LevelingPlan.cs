using Mithril.Leveling;

namespace Mithril.Planning;

/// <summary>
/// One phase of the plan: grind a single recipe for a predicted number of
/// crafts. Quantities are predictions — the #228 executor verifies them against
/// actual inventory at phase boundaries (random-output variance, externally
/// supplied ingredients, partial completion).
/// <para><see cref="IntermediateReuseXpPerCraft"/> is XP per craft credited from
/// intermediate crafts this recipe requires anyway (a sub-craft that also rewards
/// the target skill); it is folded into the phase's effective rate and surfaced
/// for explainability.</para>
/// </summary>
public sealed record PlanPhase(
    int PhaseIndex,
    string RecipeKey,
    string RecipeInternalName,
    string RecipeName,
    int IconId,
    int PredictedCrafts,
    int XpPerCraft,
    bool UsesFirstTimeBonus,
    int FirstTimeBonusXp,
    int LevelAtStart,
    int LevelAtEnd,
    int IntermediateReuseXpPerCraft = 0);

/// <summary>
/// A skill-source unlock crossed between phases: reaching this level made a new,
/// higher-value recipe available (recipe-availability gated by
/// <c>SkillLevelReq ≤ currentLevel</c>). Marked so the executor can show
/// "Phase 3 unlocks at Smithing 45 → Bronze Boots".
/// </summary>
public sealed record SkillSourceUnlock(
    int AtLevel,
    string RecipeKey,
    string RecipeInternalName,
    string RecipeName,
    int XpPerCraftAtUnlock,
    string Reason);

/// <summary>
/// A phased craft plan from current skill state to the goal, with skill-source
/// unlock events marked between phases. The plan is a prediction; the #228
/// plan-aware executor walks and re-verifies it.
/// </summary>
public sealed record LevelingPlan(
    string Skill,
    int StartLevel,
    int GoalLevel,
    long TotalXpNeeded,
    int TotalCrafts,
    IReadOnlyList<PlanPhase> Phases,
    IReadOnlyList<SkillSourceUnlock> Unlocks,
    SkillState FinalState)
{
    /// <summary>The goal is already met — empty plan, no crafts.</summary>
    public bool IsComplete => Phases.Count == 0;
}
