using Elrond.Domain;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;

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
    /// Simulate the optimal crafting path from <paramref name="character"/>'s level/XP
    /// to <paramref name="goalLevel"/> for the given skill, using the default
    /// (most-permissive) constraints.
    /// </summary>
    public SimulationResult? Simulate(string skillName, CharacterSnapshot character, int goalLevel)
        => Simulate(skillName, character, goalLevel, SimulationConstraints.Default);

    /// <summary>
    /// Simulate the optimal crafting path from <paramref name="character"/>'s level/XP
    /// to <paramref name="goalLevel"/> for the given skill, honouring <paramref name="constraints"/>.
    /// Returns <c>null</c> if the skill or character data is missing.
    /// </summary>
    public SimulationResult? Simulate(
        string skillName,
        CharacterSnapshot character,
        int goalLevel,
        SimulationConstraints constraints)
    {
        if (!character.Skills.TryGetValue(skillName, out var charSkill))
            return null;

        var xpAmounts = _engine.ResolveXpTable(skillName);
        if (xpAmounts is null) return null;

        if (goalLevel <= charSkill.Level) return MakeEmptyResult(skillName, charSkill, goalLevel, character);

        // Gather all recipes that award XP for this skill. Recipes without an
        // InternalName can't be matched against CharacterSnapshot.RecipeCompletions
        // (which keys on InternalName); the bundled data carries InternalName for
        // every XP-awarding recipe, so this is a defensive filter.
        var skillRecipes = _ref.Recipes.Values
            .Where(r => string.Equals(r.RewardSkill, skillName, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(r.InternalName)
                        && (r.RewardSkillXp > 0 || r.RewardSkillXpFirstTime > 0))
            .ToList();

        if (skillRecipes.Count == 0) return null;

        // Track which first-time bonuses are available.
        // Recipes already learned with 0 completions have the bonus ready.
        // Recipes not yet learned are assumed learnable once level req + prereq are met
        // — unless OnlyAlreadyLearnedRecipes is set, in which case we only consider
        // recipes already in RecipeCompletions.
        var unusedBonuses = new HashSet<string>(StringComparer.Ordinal);
        // Track recipes already completed (bonus consumed)
        var completedRecipes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in skillRecipes)
        {
            if (recipe.RewardSkillXpFirstTime <= 0) continue;
            if (!constraints.UseFirstTimeBonuses) continue;

            if (character.RecipeCompletions.TryGetValue(recipe.InternalName!, out var count))
            {
                if (count == 0)
                    unusedBonuses.Add(recipe.Key);
                else
                    completedRecipes.Add(recipe.InternalName!);
            }
            else if (!constraints.OnlyAlreadyLearnedRecipes)
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

        // Per-recipe completion deltas built up during the run, merged into FinalState at the end.
        // Keyed on Recipe.InternalName (matching CharacterSnapshot.RecipeCompletions).
        var deltaCompletions = new Dictionary<string, int>(StringComparer.Ordinal);

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
            // A recipe is available if level req is met AND prereq recipe (if any) has been completed,
            // AND — when OnlyAlreadyLearnedRecipes is set — it's already in the input snapshot.
            var available = skillRecipes
                .Where(r => r.SkillLevelReq <= level &&
                            (r.PrereqRecipe is null || completedRecipes.Contains(r.PrereqRecipe)) &&
                            (!constraints.OnlyAlreadyLearnedRecipes
                                || character.RecipeCompletions.ContainsKey(r.InternalName!)))
                .Select(r => (recipe: r, effXp: _engine.ComputeEffectiveXp(r, level)))
                .Where(x => x.effXp > 0 || unusedBonuses.Contains(x.recipe.Key))
                .ToList();

            if (available.Count == 0) break; // stuck, no usable recipes

            // Phase 1: Use first-time bonuses (highest bonus XP first)
            var bonusRecipes = constraints.UseFirstTimeBonuses
                ? available
                    .Where(x => unusedBonuses.Contains(x.recipe.Key))
                    .OrderByDescending(x => x.recipe.RewardSkillXpFirstTime)
                    .ToList()
                : [];

            var usedBonus = false;
            foreach (var (recipe, effXp) in bonusRecipes)
            {
                if (level >= goalLevel) break;

                var bonusXp = recipe.RewardSkillXpFirstTime;
                var levelBefore = level;

                xp += bonusXp;
                unusedBonuses.Remove(recipe.Key);
                completedRecipes.Add(recipe.InternalName!);
                AddDelta(deltaCompletions, recipe.InternalName!, 1);

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
                    recipe.Name ?? "",
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
            completedRecipes.Add(best.recipe.InternalName!);
            AddDelta(deltaCompletions, best.recipe.InternalName!, completions);

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
                best.recipe.Name ?? "",
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

        var finalState = BuildFinalState(character, skillName, charSkill, level, xp, xpForLevel, deltaCompletions);

        return new SimulationResult(
            skillName,
            startLevel,
            goalLevel,
            totalXpNeeded,
            merged.Sum(s => s.Completions),
            merged,
            finalState);
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

    private static void AddDelta(Dictionary<string, int> deltas, string internalName, int amount)
    {
        if (deltas.TryGetValue(internalName, out var existing))
            deltas[internalName] = existing + amount;
        else
            deltas[internalName] = amount;
    }

    private static CharacterSnapshot BuildFinalState(
        CharacterSnapshot input,
        string skillName,
        CharacterSkill startSkill,
        int finalLevel,
        long finalXp,
        long finalXpForLevel,
        Dictionary<string, int> deltaCompletions)
    {
        // Skills: copy and overwrite the simulated skill. BonusLevels is unchanged —
        // those come from non-XP rewards (favor, quests) the simulator doesn't model.
        var newSkills = new Dictionary<string, CharacterSkill>(input.Skills, StringComparer.Ordinal)
        {
            [skillName] = new CharacterSkill(finalLevel, startSkill.BonusLevels, finalXp, finalXpForLevel),
        };

        // RecipeCompletions: copy + add per-recipe deltas.
        var newCompletions = new Dictionary<string, int>(input.RecipeCompletions, StringComparer.Ordinal);
        foreach (var (internalName, delta) in deltaCompletions)
        {
            newCompletions.TryGetValue(internalName, out var existing);
            newCompletions[internalName] = existing + delta;
        }

        return new CharacterSnapshot(
            input.Name,
            input.Server,
            input.ExportedAt,
            newSkills,
            newCompletions,
            input.NpcFavor);
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

    private static SimulationResult MakeEmptyResult(
        string skillName, CharacterSkill startSkill, int goalLevel, CharacterSnapshot input) =>
        new(skillName, startSkill.Level, goalLevel,
            TotalXpNeeded: 0, TotalCompletions: 0, Steps: [], FinalState: input);
}
