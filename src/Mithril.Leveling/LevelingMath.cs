using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;

namespace Mithril.Leveling;

/// <summary>
/// The skill-XP contribution one craft of a recipe makes, evaluated against a
/// <see cref="SkillState"/> and <see cref="RecipeHistory"/>.
/// </summary>
public readonly record struct XpDelta(
    string RewardSkill,
    int BaseXp,
    int EffectiveXp,
    int FirstTimeBonusXp,
    bool FirstTimeBonusAvailable,
    int NextCraftXp);

/// <summary>
/// Shared skill-XP math, lifted out of Elrond per #225. Pure and time-stateless
/// (PG has no cooldowns / daily resets) but cumulative-in-completion via
/// <see cref="RecipeHistory"/> for the first-time-per-character bonus.
///
/// Owns *only* the math: drop-off curve, first-time bonus, XP-to-goal, and
/// skill→XP-table resolution. UI / sort / filter / sim policy stay in Elrond;
/// the player-progression-state constraints it runs against are supplied by the
/// caller as <see cref="SkillState"/> / <see cref="RecipeHistory"/>.
/// </summary>
public sealed class LevelingMath
{
    private readonly IReferenceDataService _ref;

    public LevelingMath(IReferenceDataService referenceData)
        => _ref = referenceData ?? throw new ArgumentNullException(nameof(referenceData));

    /// <summary>
    /// Effective XP for one craft of <paramref name="recipe"/> at
    /// <paramref name="rewardSkillLevel"/> in the recipe's reward skill, after the
    /// diminishing-returns drop-off curve.
    /// </summary>
    public int EffectiveXpPerCraft(Recipe recipe, int rewardSkillLevel)
    {
        if (recipe.RewardSkillXpDropOffLevel is not { } dropOffLevel) return recipe.RewardSkillXp;
        if (recipe.RewardSkillXpDropOffPct is not { } dropOffPct) return recipe.RewardSkillXp;

        if (rewardSkillLevel < dropOffLevel) return recipe.RewardSkillXp;

        var rate = recipe.RewardSkillXpDropOffRate ?? 1;
        if (rate <= 0) rate = 1;

        // RewardSkillXpDropOffLevel is the level AT WHICH the first reduction
        // already applies — not the level after which drop-off begins. So at
        // rewardSkillLevel == dropOffLevel we owe one reduction; the early-out above
        // covers rewardSkillLevel < dropOffLevel. Each additional 'rate' levels past
        // drop-off compounds another reduction. (Closes #159 — first tier was
        // silently skipped, making Elrond's effective XP one tier too high.)
        var levelsPast = rewardSkillLevel - dropOffLevel;
        var reductions = levelsPast / rate + 1;
        var remaining = 1.0;
        for (var i = 0; i < reductions; i++)
        {
            remaining *= (1.0 - dropOffPct);
            if (remaining <= 0) return 0;
        }

        return Math.Max(0, (int)(recipe.RewardSkillXp * remaining));
    }

    /// <summary>
    /// The XP a single craft of <paramref name="recipe"/> contributes, including
    /// whether the first-time-per-character bonus is still available for it.
    /// "Known + zero completions" is the bonus-available rule (matching Elrond's
    /// recipe-row semantics); the simulator's looser "unlearned recipes might still
    /// be learnable" treatment is a *policy* and stays with the caller.
    /// </summary>
    public XpDelta XpForCraft(Recipe recipe, SkillState skillState, RecipeHistory recipeHistory)
    {
        var rewardSkill = recipe.RewardSkill ?? "";
        var rewardLevel = skillState.LevelOf(rewardSkill);

        var internalName = recipe.InternalName ?? "";
        var isKnown = recipeHistory.IsKnown(internalName);
        var timesCompleted = recipeHistory.CompletionCount(internalName);
        var firstTimeBonusAvailable =
            isKnown && timesCompleted == 0 && recipe.RewardSkillXpFirstTime > 0;

        var effectiveXp = EffectiveXpPerCraft(recipe, rewardLevel);
        var nextCraftXp = firstTimeBonusAvailable && recipe.RewardSkillXpFirstTime > 0
            ? recipe.RewardSkillXpFirstTime
            : effectiveXp;

        return new XpDelta(
            RewardSkill: rewardSkill,
            BaseXp: recipe.RewardSkillXp,
            EffectiveXp: effectiveXp,
            FirstTimeBonusXp: recipe.RewardSkillXpFirstTime,
            FirstTimeBonusAvailable: firstTimeBonusAvailable,
            NextCraftXp: nextCraftXp);
    }

    /// <summary>
    /// Total XP needed from the current position to reach <paramref name="goalLevel"/>
    /// in <paramref name="skillName"/>.
    /// </summary>
    public long XpToGoal(
        string skillName, int currentLevel, long currentXp, long currentLevelXpNeeded, int goalLevel)
    {
        // XP remaining in the current level
        var total = currentLevelXpNeeded - currentXp;
        if (total < 0) total = 0;

        var xpAmounts = ResolveXpTable(skillName);
        if (xpAmounts is null) return total;

        // Add XP for each full level between currentLevel+1 and goalLevel-1,
        // plus XP for goalLevel-1 index (to reach goalLevel)
        for (var lvl = currentLevel + 1; lvl < goalLevel; lvl++)
        {
            if (lvl - 1 < xpAmounts.Count)
                total += xpAmounts[lvl - 1];
            else
                break;
        }

        return total;
    }

    /// <summary>
    /// XP amounts array for <paramref name="skillName"/> (index 0 = XP for level 1),
    /// or <c>null</c> when the skill has no XP table (umbrella skills).
    /// </summary>
    public IReadOnlyList<long>? ResolveXpTable(string skillName)
    {
        if (_ref.Skills.TryGetValue(skillName, out var skillEntry) &&
            !string.IsNullOrEmpty(skillEntry.XpTable) &&
            _ref.XpTables.TryGetValue(skillEntry.XpTable, out var xpTable))
        {
            return xpTable.XpAmounts;
        }
        return null;
    }
}
