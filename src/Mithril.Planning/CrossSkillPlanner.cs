using Mithril.Leveling;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Crafting;
using Mithril.Shared.Reference;

namespace Mithril.Planning;

/// <summary>
/// The cross-skill leveling planner (#227, engine-only — the UI surface is a
/// separate follow-up). Takes a player's skill state and a goal and returns a
/// <see cref="LevelingPlan"/>: an ordered sequence of grind phases with
/// skill-source unlock events marked between them.
///
/// Closes the loop Elrond's forward-simulator leaves open — it *discovers* which
/// recipe to grind (auto-considering recipes that unlock as the skill rises),
/// credits intermediate crafts you have to make anyway, and emits explicit
/// recipe-switch phases when a higher-XP recipe unlocks mid-grind.
///
/// v1 scope (per the issue): single scalar skill target; objective is pure craft
/// count (acquisition-cost weighting / #122 is an explicit follow-up). The
/// multi-skill substrate (<see cref="SkillState"/>) is present so vector targets
/// are an additive follow-up, not a rewrite.
/// </summary>
public sealed class CrossSkillPlanner
{
    private readonly IReferenceDataService _ref;
    private readonly LevelingMath _math;
    private readonly RecipeExpander _expander;

    /// <summary>Depth-1 intermediate-reuse credit for v1 — deeper DAG reuse is a follow-up.</summary>
    private const int ReuseExpansionDepth = 1;

    public CrossSkillPlanner(IReferenceDataService referenceData, LevelingMath math, RecipeExpander? expander = null)
    {
        _ref = referenceData ?? throw new ArgumentNullException(nameof(referenceData));
        _math = math ?? throw new ArgumentNullException(nameof(math));
        _expander = expander ?? new RecipeExpander(referenceData);
    }

    /// <summary>
    /// Plan from <paramref name="state"/> to <paramref name="target"/>. Returns
    /// <c>null</c> when the skill has no XP table (umbrella skills can't be
    /// planned) or the skill is absent from the state.
    /// </summary>
    public LevelingPlan? Plan(
        SkillTarget target,
        SkillState state,
        RecipeHistory history,
        IReadOnlyDictionary<string, int>? onHand = null,
        SourcingPolicy? sourcing = null,
        AssertedUnlocks? asserted = null)
    {
        sourcing ??= SourcingPolicy.CraftEverything;
        asserted ??= AssertedUnlocks.None;
        onHand ??= new Dictionary<string, int>(StringComparer.Ordinal);

        var skill = target.Skill;
        var xpAmounts = _math.ResolveXpTable(skill);
        if (xpAmounts is null) return null;
        if (!state.TryGet(skill, out var start)) return null;

        var startLevel = start.Level;
        if (target.GoalLevel <= startLevel)
            return new LevelingPlan(skill, startLevel, target.GoalLevel,
                TotalXpNeeded: 0, TotalCrafts: 0, Phases: [], Unlocks: [], FinalState: state);

        var totalXpNeeded = _math.XpToGoal(
            skill, start.Level, start.XpTowardNextLevel, start.XpNeededForNextLevel, target.GoalLevel);

        // Candidate recipes: award XP in the target skill, have an InternalName,
        // and their output isn't policy-Ignored.
        var candidates = _ref.Recipes.Values
            .Where(r => string.Equals(r.RewardSkill, skill, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(r.InternalName)
                        && (r.RewardSkillXp > 0 || r.RewardSkillXpFirstTime > 0)
                        && !IsOutputIgnored(r, sourcing))
            .ToList();
        if (candidates.Count == 0) return null;

        // Mutable run state.
        var completions = new Dictionary<string, int>(history.Completions, StringComparer.Ordinal);
        var completed = new HashSet<string>(
            history.Completions.Where(kv => kv.Value > 0).Select(kv => kv.Key), StringComparer.Ordinal);
        var unusedBonuses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in candidates)
        {
            if (r.RewardSkillXpFirstTime <= 0) continue;
            var name = r.InternalName!;
            if (history.IsKnown(name))
            {
                if (history.CompletionCount(name) == 0) unusedBonuses.Add(r.Key);
            }
            else if (asserted.IsAsserted(name) || r.SkillLevelReq > 0)
            {
                // Auto-considered: a skill-gated recipe's first craft still carries
                // its one-shot bonus once we reach the gate.
                unusedBonuses.Add(r.Key);
            }
        }

        var level = start.Level;
        var xp = start.XpTowardNextLevel;
        var xpForLevel = start.XpNeededForNextLevel;

        var rawSteps = new List<PlanPhase>();
        var unlocks = new List<SkillSourceUnlock>();
        var unlockEmitted = new HashSet<string>(StringComparer.Ordinal);
        var reuseCache = new Dictionary<(string, int), int>();

        // Seed: any skill-gated recipe already past its gate at the start level is
        // not an "unlock event" — only crossings *during* the plan are marked.
        foreach (var r in candidates)
            if (IsAvailable(r, level, state, completed, history, asserted))
                unlockEmitted.Add(r.Key);

        var guard = 0;
        while (level < target.GoalLevel && guard++ < 100_000)
        {
            if (xp >= xpForLevel)
            {
                xp -= xpForLevel;
                level++;
                if (level - 1 < xpAmounts.Count) xpForLevel = xpAmounts[level - 1];
                EmitNewUnlocks(candidates, level, state, completed, history, asserted,
                    unlockEmitted, unlocks, reuseCache);
                continue;
            }

            var available = candidates
                .Where(r => IsAvailable(r, level, state, completed, history, asserted))
                .Select(r =>
                {
                    var baseXp = _math.EffectiveXpPerCraft(r, level);
                    var reuse = IntermediateReuseXpPerCraft(r, level, state, completed, history, asserted, onHand, sourcing, reuseCache);
                    return (recipe: r, baseXp, reuse, effXp: baseXp + reuse);
                })
                .Where(x => x.effXp > 0 || unusedBonuses.Contains(x.recipe.Key))
                // Respect per-character craft caps — Recipe.MaxUses and the
                // OtherRequirements RecipeUsed gate — against prior history +
                // this run. A recipe with no budget left can't be a grind OR a
                // bonus source. (AlwaysFail / RecipeKnown gates live in IsAvailable.)
                .Where(x => RemainingCraftBudget(x.recipe, completions) > 0)
                .ToList();

            if (available.Count == 0) break;

            // Phase 1: spend the highest first-time bonus available.
            var bonus = available
                .Where(x => unusedBonuses.Contains(x.recipe.Key) && x.recipe.RewardSkillXpFirstTime > 0)
                .OrderByDescending(x => x.recipe.RewardSkillXpFirstTime)
                .ThenBy(x => x.recipe.Key, StringComparer.Ordinal)
                .FirstOrDefault();

            if (bonus.recipe is not null)
            {
                var bonusXp = bonus.recipe.RewardSkillXpFirstTime;
                var before = level;
                xp += bonusXp;
                unusedBonuses.Remove(bonus.recipe.Key);
                completed.Add(bonus.recipe.InternalName!);
                Bump(completions, bonus.recipe.InternalName!, 1);
                while (xp >= xpForLevel && level < target.GoalLevel)
                {
                    xp -= xpForLevel;
                    level++;
                    if (level - 1 < xpAmounts.Count) xpForLevel = xpAmounts[level - 1]; else break;
                }
                EmitNewUnlocks(candidates, level, state, completed, history, asserted,
                    unlockEmitted, unlocks, reuseCache);
                rawSteps.Add(new PlanPhase(0, bonus.recipe.Key, bonus.recipe.InternalName!,
                    bonus.recipe.Name ?? "", bonus.recipe.IconId, PredictedCrafts: 1,
                    XpPerCraft: bonus.baseXp, UsesFirstTimeBonus: true, FirstTimeBonusXp: bonusXp,
                    LevelAtStart: before, LevelAtEnd: level, IntermediateReuseXpPerCraft: bonus.reuse));
                continue;
            }

            // Phase 2: grind the best effective-XP recipe toward the next level.
            var best = available
                .OrderByDescending(x => x.effXp)
                .ThenBy(x => x.recipe.Key, StringComparer.Ordinal)
                .First();
            if (best.effXp <= 0) break;

            var grindBefore = level;
            var needed = xpForLevel - xp;
            var crafts = (int)Math.Ceiling((double)needed / best.effXp);
            if (crafts < 1) crafts = 1;
            // Never schedule a recipe past its craft budget (MaxUses + RecipeUsed);
            // if that's short of the level, the loop re-picks the next-best recipe
            // next iteration (this recipe is now exhausted ⇒ filtered out).
            var remaining = RemainingCraftBudget(best.recipe, completions);
            if (crafts > remaining) crafts = remaining;

            xp += (long)crafts * best.effXp;
            completed.Add(best.recipe.InternalName!);
            Bump(completions, best.recipe.InternalName!, crafts);
            while (xp >= xpForLevel && level < target.GoalLevel)
            {
                xp -= xpForLevel;
                level++;
                if (level - 1 < xpAmounts.Count) xpForLevel = xpAmounts[level - 1]; else break;
            }
            EmitNewUnlocks(candidates, level, state, completed, history, asserted,
                unlockEmitted, unlocks, reuseCache);
            rawSteps.Add(new PlanPhase(0, best.recipe.Key, best.recipe.InternalName!,
                best.recipe.Name ?? "", best.recipe.IconId, PredictedCrafts: crafts,
                XpPerCraft: best.baseXp, UsesFirstTimeBonus: false, FirstTimeBonusXp: 0,
                LevelAtStart: grindBefore, LevelAtEnd: level, IntermediateReuseXpPerCraft: best.reuse));
        }

        var phases = MergeAndNumber(rawSteps);

        // Goal not met and nothing could be ground (every candidate gated/unknown/
        // unasserted, or all recipes give 0 effective XP) → no viable path. This is
        // distinct from "goal already met" (handled above as a complete empty plan).
        if (phases.Count == 0 && level < target.GoalLevel) return null;

        var finalSkills = new Dictionary<string, SkillProgress>(state.Skills, StringComparer.Ordinal)
        {
            [skill] = new SkillProgress(level, start.BonusLevels, xp, xpForLevel),
        };

        return new LevelingPlan(
            skill, startLevel, target.GoalLevel, totalXpNeeded,
            TotalCrafts: phases.Sum(p => p.PredictedCrafts),
            Phases: phases, Unlocks: unlocks, FinalState: new SkillState(finalSkills));
    }

    private bool IsOutputIgnored(Recipe recipe, SourcingPolicy sourcing)
    {
        foreach (var output in EnumerateOutputs(recipe))
            if (sourcing.For(output) == SourcingMode.Ignore) return true;
        return false;
    }

    private IEnumerable<string> EnumerateOutputs(Recipe recipe)
    {
        IEnumerable<RecipeResultItem> All()
        {
            if (recipe.ResultItems is { } r) foreach (var x in r) yield return x;
            if (recipe.ProtoResultItems is { } p) foreach (var x in p) yield return x;
        }
        foreach (var result in All())
            if (_ref.Items.TryGetValue(result.ItemCode, out var item) && !string.IsNullOrEmpty(item.InternalName))
                yield return item.InternalName!;
    }

    /// <summary>
    /// A recipe is grindable when its skill gate is met, its prereq is complete,
    /// and it's either known, user-asserted, or skill-gated (skill-source unlocks
    /// are auto-considered — that's the planner's job vs. Elrond's sim).
    /// </summary>
    private bool IsAvailable(
        Recipe recipe, int targetSkillLevel, SkillState state,
        ISet<string> completed, RecipeHistory history, AssertedUnlocks asserted)
    {
        var name = recipe.InternalName ?? "";
        var gatingSkill = string.IsNullOrEmpty(recipe.Skill) ? null : recipe.Skill;
        int gatingLevel;
        if (gatingSkill is null || string.Equals(gatingSkill, recipe.RewardSkill, StringComparison.Ordinal))
            gatingLevel = targetSkillLevel; // gate sits on the skill we're levelling
        else
            gatingLevel = state.LevelOf(gatingSkill); // cross-gated; static in v1 (single target)

        if (recipe.SkillLevelReq > gatingLevel) return false;
        if (!string.IsNullOrEmpty(recipe.PrereqRecipe) && !completed.Contains(recipe.PrereqRecipe!)) return false;

        // OtherRequirements gates the planner can resolve from data it already
        // has. RecipeUsed (per-character craft cap) is dynamic ⇒ enforced in the
        // RemainingCraftBudget path, not here. Non-skill gates (pet/form/buff/
        // location/event) are a deliberate punt to AssertedUnlocks — see
        // docs/planner-recipe-field-consumption.md.
        if (recipe.OtherRequirements is { } reqs)
        {
            foreach (var req in reqs)
            {
                // Can never succeed (the ImproveProphesied* recipes) — must never
                // be scheduled despite advertising large XP.
                if (req is AlwaysFailRequirement) return false;
                // "Recipe X must be known" — same shape as the PrereqRecipe gate.
                if (req is RecipeKnownRequirement { Recipe: { } needed }
                    && !completed.Contains(needed) && !history.IsKnown(needed))
                    return false;
            }
        }

        return history.IsKnown(name) || asserted.IsAsserted(name) || recipe.SkillLevelReq > 0;
    }

    private void EmitNewUnlocks(
        IReadOnlyList<Recipe> candidates, int level, SkillState state,
        ISet<string> completed, RecipeHistory history, AssertedUnlocks asserted,
        ISet<string> unlockEmitted, List<SkillSourceUnlock> unlocks,
        Dictionary<(string, int), int> reuseCache)
    {
        foreach (var r in candidates)
        {
            if (unlockEmitted.Contains(r.Key)) continue;
            if (r.SkillLevelReq <= 0) continue; // only skill-source unlocks are auto-marked
            if (!IsAvailable(r, level, state, completed, history, asserted)) continue;

            unlockEmitted.Add(r.Key);
            unlocks.Add(new SkillSourceUnlock(
                AtLevel: level,
                RecipeKey: r.Key,
                RecipeInternalName: r.InternalName!,
                RecipeName: r.Name ?? "",
                XpPerCraftAtUnlock: _math.EffectiveXpPerCraft(r, level),
                Reason: $"Reaches {r.Skill ?? r.RewardSkill} {r.SkillLevelReq}"));
        }
    }

    /// <summary>
    /// XP/craft credited from sub-crafts a single craft of <paramref name="recipe"/>
    /// requires anyway. Depth-1 for v1: expand the recipe's ingredients once; any
    /// ingredient produced by a recipe that also rewards the target skill (and is
    /// available) contributes its XP, weighted by how many sub-batches one craft
    /// needs. Sourcing-pruned and on-hand-aware via the shared expander.
    /// </summary>
    private int IntermediateReuseXpPerCraft(
        Recipe recipe, int level, SkillState state, ISet<string> completed,
        RecipeHistory history, AssertedUnlocks asserted,
        IReadOnlyDictionary<string, int> onHand, SourcingPolicy sourcing,
        Dictionary<(string, int), int> cache)
    {
        if (cache.TryGetValue((recipe.Key, level), out var cached)) return cached;
        cache[(recipe.Key, level)] = 0; // cycle guard while computing

        var seed = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var output in EnumerateOutputs(recipe)) { seed[output] = 1; break; }
        if (seed.Count == 0) return 0;

        // Prune sub-DAGs the user supplies externally / ignores by treating them as
        // effectively in-stock so the expander won't recurse into them.
        var effOnHand = new Dictionary<string, int>(onHand, StringComparer.Ordinal);
        foreach (var (item, mode) in sourcing.ByItemInternalName)
            if (mode is SourcingMode.SupplyExternally or SourcingMode.Ignore)
                effOnHand[item] = int.MaxValue;

        var demand = new Dictionary<string, double>(seed, StringComparer.Ordinal);
        _expander.Expand(demand, ReuseExpansionDepth, effOnHand, null,
            new Dictionary<string, KeywordSlot>());

        var credit = 0;
        foreach (var (itemName, qty) in demand)
        {
            if (RecipeExpander.IsKeywordKey(itemName)) continue;
            if (string.Equals(itemName, seed.Keys.First(), StringComparison.Ordinal)) continue;
            if (qty <= 0) continue;
            if (!_expander.Producers.TryGetDefault(itemName, out var sub)) continue;
            if (!string.Equals(sub.RewardSkill, recipe.RewardSkill, StringComparison.Ordinal)) continue;
            if (!IsAvailable(sub, level, state, completed, history, asserted)) continue;

            var stack = Math.Max(1, _expander.OutputStackSize(sub, itemName));
            var subBatches = (int)Math.Ceiling(qty / stack);
            credit += subBatches * _math.EffectiveXpPerCraft(sub, level);
        }

        cache[(recipe.Key, level)] = credit;
        return credit;
    }

    private static void Bump(IDictionary<string, int> map, string key, int by)
        => map[key] = map.TryGetValue(key, out var v) ? v + by : by;

    /// <summary>
    /// How many more times this recipe may be crafted given its per-character
    /// lifetime cap (<see cref="Recipe.MaxUses"/> — Research recipes:
    /// WeatherWitching / FireMagic / IceMagic, often <c>1</c>). <paramref name="completions"/>
    /// is seeded from <see cref="RecipeHistory"/> and bumped per scheduled
    /// craft, so it already holds prior + this-run uses. No cap ⇒ unbounded.
    /// </summary>
    private static int RemainingUses(Recipe recipe, IReadOnlyDictionary<string, int> completions)
        => recipe.MaxUses is int max
            ? Math.Max(0, max - (completions.TryGetValue(recipe.InternalName ?? "", out var c) ? c : 0))
            : int.MaxValue;

    /// <summary>
    /// Craft allowance from the <c>OtherRequirements</c> <see cref="RecipeUsedRequirement"/>
    /// gates: each says "the referenced recipe must have been used ≤ MaxTimesUsed".
    /// Self-referential ones (the WeatherWitching litany — recipe X requires
    /// <c>RecipeUsed{X, n}</c>) are a per-character cap of <c>n + 1</c> crafts;
    /// cross-referential ones are a static gate (0 once the other recipe is over
    /// its cap, unbounded otherwise — this recipe doesn't increment it). Min over
    /// all such gates; no gate ⇒ unbounded.
    /// </summary>
    private static int OtherReqUsesRemaining(Recipe recipe, IReadOnlyDictionary<string, int> completions)
    {
        if (recipe.OtherRequirements is not { } reqs) return int.MaxValue;
        var budget = int.MaxValue;
        foreach (var r in reqs)
        {
            if (r is not RecipeUsedRequirement { Recipe: { } target } used) continue;
            var max = used.MaxTimesUsed ?? 0;
            var done = completions.TryGetValue(target, out var c) ? c : 0;
            budget = Math.Min(budget, Math.Max(0, max + 1 - done));
        }
        return budget;
    }

    /// <summary>The min of every per-character craft cap that applies (MaxUses +
    /// RecipeUsed). Zero ⇒ the recipe is exhausted and must be skipped.</summary>
    private static int RemainingCraftBudget(Recipe recipe, IReadOnlyDictionary<string, int> completions)
        => Math.Min(RemainingUses(recipe, completions), OtherReqUsesRemaining(recipe, completions));

    /// <summary>
    /// Merge consecutive non-bonus steps for the same recipe into one phase, then
    /// assign sequential <see cref="PlanPhase.PhaseIndex"/>.
    /// </summary>
    private static IReadOnlyList<PlanPhase> MergeAndNumber(List<PlanPhase> steps)
    {
        var merged = new List<PlanPhase>();
        foreach (var step in steps)
        {
            if (merged.Count > 0)
            {
                var prev = merged[^1];
                if (prev.RecipeKey == step.RecipeKey
                    && !prev.UsesFirstTimeBonus && !step.UsesFirstTimeBonus
                    && prev.XpPerCraft == step.XpPerCraft
                    && prev.IntermediateReuseXpPerCraft == step.IntermediateReuseXpPerCraft)
                {
                    merged[^1] = prev with
                    {
                        PredictedCrafts = prev.PredictedCrafts + step.PredictedCrafts,
                        LevelAtEnd = step.LevelAtEnd,
                    };
                    continue;
                }
            }
            merged.Add(step);
        }

        for (var i = 0; i < merged.Count; i++)
            merged[i] = merged[i] with { PhaseIndex = i };
        return merged;
    }
}
