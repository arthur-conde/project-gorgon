using Elrond.Domain;
using Gorgon.Shared.Character;
using Gorgon.Shared.Reference;

namespace Elrond.Services;

/// <summary>
/// Greedy level-by-level simulator that finds the minimum-completion path
/// to a goal level by leveraging first-time bonuses and picking the best
/// effective-XP recipe at each level.
/// </summary>
public sealed class LevelingSimulator
{
    private readonly IReferenceDataService _ref;
    private readonly SkillAdvisorEngine _engine;

    public LevelingSimulator(IReferenceDataService referenceData, SkillAdvisorEngine engine)
    {
        _ref = referenceData;
        _engine = engine;
    }

    /// <summary>
    /// Simulate the optimal crafting path from the character's current level/XP
    /// to <paramref name="goalLevel"/> for the given skill.
    /// Returns <c>null</c> if the skill or character data is missing.
    /// </summary>
    public SimulationResult? Simulate(string skillName, CharacterSnapshot character, int goalLevel)
    {
        if (!character.Skills.TryGetValue(skillName, out var charSkill))
            return null;

        var xpAmounts = _engine.ResolveXpTable(skillName);
        if (xpAmounts is null) return null;

        if (goalLevel <= charSkill.Level) return MakeEmptyResult(skillName, charSkill.Level, goalLevel);

        // Gather all recipes that award XP for this skill
        var skillRecipes = _ref.Recipes.Values
            .Where(r => r.RewardSkill.Equals(skillName, StringComparison.Ordinal)
                        && (r.RewardSkillXp > 0 || r.RewardSkillXpFirstTime > 0))
            .ToList();

        if (skillRecipes.Count == 0) return null;

        // Track which first-time bonuses are available.
        // Recipes already learned with 0 completions have the bonus ready.
        // Recipes not yet learned are assumed learnable once level req + prereq are met.
        var unusedBonuses = new HashSet<string>(StringComparer.Ordinal);
        // Track recipes already completed (bonus consumed)
        var completedRecipes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in skillRecipes)
        {
            if (recipe.RewardSkillXpFirstTime <= 0) continue;

            if (character.RecipeCompletions.TryGetValue(recipe.InternalName, out var count))
            {
                if (count == 0)
                    unusedBonuses.Add(recipe.Key);
                else
                    completedRecipes.Add(recipe.InternalName);
            }
            else
            {
                // Not yet learned — will become available when prereqs are met
                unusedBonuses.Add(recipe.Key);
            }
        }
        // Also track all recipes that are already completed (for prereq resolution)
        foreach (var (internalName, count) in character.RecipeCompletions)
        {
            if (count > 0)
                completedRecipes.Add(internalName);
        }

        // Simulation state
        var level = charSkill.Level;
        var xp = charSkill.XpTowardNextLevel;
        var xpForLevel = charSkill.XpNeededForNextLevel;
        var startLevel = level;
        var totalXpNeeded = _engine.ComputeXpToGoal(skillName, level, xp, xpForLevel, goalLevel);
        var steps = new List<SimulationStep>();

        while (level < goalLevel)
        {
            var xpToNextLevel = xpForLevel - xp;
            if (xpToNextLevel <= 0)
            {
                // Level up
                if (!TryLevelUp(xpAmounts, ref level, ref xp, ref xpForLevel))
                    break; // beyond XP table
                continue;
            }

            // Get recipes available at this level, with effective XP.
            // A recipe is available if level req is met AND prereq recipe (if any) has been completed.
            var available = skillRecipes
                .Where(r => r.SkillLevelReq <= level &&
                            (r.PrereqRecipe is null || completedRecipes.Contains(r.PrereqRecipe)))
                .Select(r => (recipe: r, effXp: _engine.ComputeEffectiveXp(r, level)))
                .Where(x => x.effXp > 0 || unusedBonuses.Contains(x.recipe.Key))
                .ToList();

            if (available.Count == 0) break; // stuck, no usable recipes

            // Phase 1: Use first-time bonuses (highest bonus XP first)
            var bonusRecipes = available
                .Where(x => unusedBonuses.Contains(x.recipe.Key))
                .OrderByDescending(x => x.recipe.RewardSkillXpFirstTime)
                .ToList();

            var usedBonus = false;
            foreach (var (recipe, effXp) in bonusRecipes)
            {
                if (level >= goalLevel) break;

                var bonusXp = recipe.RewardSkillXpFirstTime;
                var levelBefore = level;

                xp += bonusXp;
                unusedBonuses.Remove(recipe.Key);
                completedRecipes.Add(recipe.InternalName);

                // Handle level-ups from this single bonus craft
                var levelAfter = level;
                while (xp >= xpForLevel && levelAfter < goalLevel)
                {
                    xp -= xpForLevel;
                    levelAfter++;
                    if (levelAfter - 1 < xpAmounts.Count)
                        xpForLevel = xpAmounts[levelAfter - 1];
                    else
                        break;
                }
                level = levelAfter;

                steps.Add(new SimulationStep(
                    recipe.Key,
                    recipe.Name,
                    recipe.IconId,
                    Completions: 1,
                    XpPerCompletion: effXp,
                    UsesFirstTimeBonus: true,
                    FirstTimeBonusXp: bonusXp,
                    TotalXpFromStep: bonusXp,
                    LevelAtStart: levelBefore,
                    LevelAtEnd: level));

                usedBonus = true;
                break; // re-evaluate available recipes after potential level-up
            }

            if (usedBonus) continue;

            // Phase 2: Grind the best effective-XP recipe toward next level
            var best = available.OrderByDescending(x => x.effXp).First();
            var grindEffXp = best.effXp;

            if (grindEffXp <= 0) break; // all recipes give 0 XP

            var grindLevelBefore = level;
            var xpNeeded = xpForLevel - xp;
            var completions = (int)Math.Ceiling((double)xpNeeded / grindEffXp);
            if (completions < 1) completions = 1;

            var totalGrindXp = (long)completions * grindEffXp;
            xp += totalGrindXp;
            completedRecipes.Add(best.recipe.InternalName);

            // Handle level-ups
            while (xp >= xpForLevel && level < goalLevel)
            {
                xp -= xpForLevel;
                level++;
                if (level - 1 < xpAmounts.Count)
                    xpForLevel = xpAmounts[level - 1];
                else
                    break;
            }

            steps.Add(new SimulationStep(
                best.recipe.Key,
                best.recipe.Name,
                best.recipe.IconId,
                completions,
                XpPerCompletion: grindEffXp,
                UsesFirstTimeBonus: false,
                FirstTimeBonusXp: 0,
                TotalXpFromStep: totalGrindXp,
                LevelAtStart: grindLevelBefore,
                LevelAtEnd: level));
        }

        // Merge consecutive steps for the same recipe (non-bonus) to keep the list clean
        var merged = MergeConsecutiveSteps(steps);

        return new SimulationResult(
            skillName,
            startLevel,
            goalLevel,
            totalXpNeeded,
            merged.Sum(s => s.Completions),
            merged);
    }

    private static bool TryLevelUp(IReadOnlyList<long> xpAmounts, ref int level, ref long xp, ref long xpForLevel)
    {
        xp -= xpForLevel;
        level++;
        if (level - 1 < xpAmounts.Count)
        {
            xpForLevel = xpAmounts[level - 1];
            return true;
        }
        return false;
    }

    private static List<SimulationStep> MergeConsecutiveSteps(List<SimulationStep> steps)
    {
        if (steps.Count <= 1) return steps;

        var merged = new List<SimulationStep> { steps[0] };
        for (var i = 1; i < steps.Count; i++)
        {
            var prev = merged[^1];
            var curr = steps[i];

            // Merge if same recipe, both non-bonus, same XP per completion
            if (curr.RecipeKey == prev.RecipeKey &&
                !curr.UsesFirstTimeBonus && !prev.UsesFirstTimeBonus &&
                curr.XpPerCompletion == prev.XpPerCompletion)
            {
                merged[^1] = prev with
                {
                    Completions = prev.Completions + curr.Completions,
                    TotalXpFromStep = prev.TotalXpFromStep + curr.TotalXpFromStep,
                    LevelAtEnd = curr.LevelAtEnd,
                };
            }
            else
            {
                merged.Add(curr);
            }
        }
        return merged;
    }

    private static SimulationResult MakeEmptyResult(string skillName, int currentLevel, int goalLevel) =>
        new(skillName, currentLevel, goalLevel, TotalXpNeeded: 0, TotalCompletions: 0, Steps: []);
}
