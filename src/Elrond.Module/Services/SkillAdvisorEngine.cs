using Elrond.Domain;
using Gorgon.Shared.Character;
using Gorgon.Shared.Reference;

namespace Elrond.Services;

/// <summary>
/// Pure computation engine that cross-references CDN reference data with a
/// character snapshot to produce skill-leveling analysis.
/// </summary>
public sealed class SkillAdvisorEngine
{
    private readonly IReferenceDataService _ref;

    public SkillAdvisorEngine(IReferenceDataService referenceData)
    {
        _ref = referenceData;
    }

    /// <summary>
    /// Returns all skill names that have at least one recipe awarding XP,
    /// sorted alphabetically.
    /// </summary>
    public IReadOnlyList<string> GetSkillsWithRecipes()
    {
        var skills = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in _ref.Recipes.Values)
        {
            if (!string.IsNullOrEmpty(recipe.RewardSkill) && recipe.RewardSkillXp > 0)
                skills.Add(recipe.RewardSkill);
        }
        var list = skills.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>
    /// Analyze a skill for a given character: available recipes, XP rewards,
    /// first-time bonuses, and leveling milestones.
    /// When <paramref name="goalLevel"/> is set, XP remaining and completions-to-level
    /// reflect the total gap from current progress to that goal.
    /// </summary>
    public SkillAnalysis? Analyze(string skillName, CharacterSnapshot character, bool includeZeroXp = false, int? goalLevel = null)
    {
        if (!character.Skills.TryGetValue(skillName, out var charSkill))
            return null;

        var currentLevel = charSkill.Level;
        var currentXp = charSkill.XpTowardNextLevel;
        var xpNeeded = charSkill.XpNeededForNextLevel;

        long xpRemaining;
        if (goalLevel is { } goal && goal > currentLevel)
            xpRemaining = ComputeXpToGoal(skillName, currentLevel, currentXp, xpNeeded, goal);
        else
            xpRemaining = xpNeeded - currentXp;

        if (xpRemaining < 0) xpRemaining = 0;

        // Find all recipes that reward this skill
        var recipeAnalyses = new List<RecipeAnalysis>();
        foreach (var recipe in _ref.Recipes.Values)
        {
            if (!recipe.RewardSkill.Equals(skillName, StringComparison.Ordinal)) continue;
            if (recipe.RewardSkillXp <= 0 && recipe.RewardSkillXpFirstTime <= 0) continue;
            if (!includeZeroXp && recipe.RewardSkillXp <= 0) continue;

            var isKnown = character.RecipeCompletions.TryGetValue(recipe.InternalName, out var timesCompleted);
            var firstTimeBonusAvailable = isKnown && timesCompleted == 0 && recipe.RewardSkillXpFirstTime > 0;

            var effectiveXp = ComputeEffectiveXp(recipe, currentLevel);

            int? completionsToLevel = null;
            if (effectiveXp > 0 && xpRemaining > 0)
            {
                if (firstTimeBonusAvailable && recipe.RewardSkillXpFirstTime > 0)
                {
                    // First completion uses first-time bonus XP
                    var afterFirst = xpRemaining - recipe.RewardSkillXpFirstTime;
                    if (afterFirst <= 0)
                        completionsToLevel = 1;
                    else
                        completionsToLevel = 1 + (int)Math.Ceiling((double)afterFirst / effectiveXp);
                }
                else
                {
                    completionsToLevel = (int)Math.Ceiling((double)xpRemaining / effectiveXp);
                }
            }

            var ingredients = recipe.Ingredients
                .Select(i => _ref.Items.TryGetValue(i.ItemCode, out var item)
                    ? new RecipeIngredientDisplay(item.Name, item.IconId, i.StackSize, i.ChanceToConsume)
                    : new RecipeIngredientDisplay($"Item #{i.ItemCode}", 0, i.StackSize, i.ChanceToConsume))
                .ToList();

            var craftedOutputs = ResultEffectsParser.ParseCraftedGear(recipe.ResultEffects, _ref);

            recipeAnalyses.Add(new RecipeAnalysis(
                recipe.Key,
                recipe.Name,
                recipe.InternalName,
                recipe.IconId,
                recipe.SkillLevelReq,
                recipe.RewardSkillXp,
                recipe.RewardSkillXpFirstTime,
                timesCompleted,
                isKnown,
                firstTimeBonusAvailable,
                effectiveXp,
                completionsToLevel,
                ingredients,
                craftedOutputs));
        }

        // Sort: level required ascending, then effective XP descending
        recipeAnalyses.Sort((a, b) =>
        {
            var cmp = a.LevelRequired.CompareTo(b.LevelRequired);
            if (cmp != 0) return cmp;
            return b.EffectiveXp.CompareTo(a.EffectiveXp);
        });

        // Build XP milestones for next ~10 levels
        var milestones = BuildMilestones(skillName, currentLevel, currentXp, xpNeeded, maxLevels: 10);

        return new SkillAnalysis(
            skillName,
            currentLevel,
            currentXp,
            xpNeeded,
            xpRemaining,
            recipeAnalyses,
            milestones,
            goalLevel is { } g && g > currentLevel ? goalLevel : null);
    }

    internal int ComputeEffectiveXp(RecipeEntry recipe, int playerLevel)
    {
        if (recipe.RewardSkillXpDropOffLevel is not { } dropOffLevel) return recipe.RewardSkillXp;
        if (recipe.RewardSkillXpDropOffPct is not { } dropOffPct) return recipe.RewardSkillXp;

        if (playerLevel < dropOffLevel) return recipe.RewardSkillXp;

        var rate = recipe.RewardSkillXpDropOffRate ?? 1;
        if (rate <= 0) rate = 1;

        // Each 'rate' levels past drop-off reduces by dropOffPct
        var levelsPast = playerLevel - dropOffLevel;
        var reductions = levelsPast / rate;
        var remaining = 1.0f;
        for (var i = 0; i < reductions; i++)
        {
            remaining *= (1.0f - dropOffPct);
            if (remaining <= 0) return 0;
        }

        return Math.Max(0, (int)(recipe.RewardSkillXp * remaining));
    }

    /// <summary>
    /// Computes the total XP needed from the current position to reach <paramref name="goalLevel"/>.
    /// </summary>
    internal long ComputeXpToGoal(string skillName, int currentLevel, long currentXp, long currentLevelXpNeeded, int goalLevel)
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

    /// <summary>Resolves the XP amounts array for a given skill.</summary>
    internal IReadOnlyList<long>? ResolveXpTable(string skillName)
    {
        if (_ref.Skills.TryGetValue(skillName, out var skillEntry) &&
            !string.IsNullOrEmpty(skillEntry.XpTable) &&
            _ref.XpTables.TryGetValue(skillEntry.XpTable, out var xpTable))
        {
            return xpTable.XpAmounts;
        }
        return null;
    }

    private IReadOnlyList<XpMilestone> BuildMilestones(
        string skillName, int currentLevel, long currentXp, long currentLevelXpNeeded, int maxLevels)
    {
        // Resolve XP table for the skill
        IReadOnlyList<long>? xpAmounts = null;
        if (_ref.Skills.TryGetValue(skillName, out var skillEntry) &&
            !string.IsNullOrEmpty(skillEntry.XpTable) &&
            _ref.XpTables.TryGetValue(skillEntry.XpTable, out var xpTable))
        {
            xpAmounts = xpTable.XpAmounts;
        }

        var milestones = new List<XpMilestone>();
        var cumulative = currentLevelXpNeeded - currentXp; // XP remaining in current level

        for (var lvl = currentLevel + 1; lvl <= currentLevel + maxLevels; lvl++)
        {
            // XpAmounts is 0-indexed: index 0 = XP for level 1, index N-1 = XP for level N
            long xpForLevel;
            if (xpAmounts is not null && lvl - 1 < xpAmounts.Count)
                xpForLevel = xpAmounts[lvl - 1];
            else
                break; // no data for this level

            milestones.Add(new XpMilestone(lvl, xpForLevel, cumulative));
            cumulative += xpForLevel;
        }

        return milestones;
    }
}
