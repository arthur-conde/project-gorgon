using Celebrimbor.Domain;
using Mithril.Leveling;
using Mithril.Planning;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;

namespace Celebrimbor.Services;

/// <summary>One phase's completion check against actual inventory.</summary>
public sealed record PhaseEvaluation(
    string OutputInternalName,
    int Threshold,
    int OnHand,
    bool IsComplete);

/// <summary>Where the walk currently stands across the whole plan.</summary>
public sealed record PlanWalkState(
    int CurrentPhaseIndex,
    PersistedPlanPhase? CurrentPhase,
    PhaseEvaluation? CurrentEvaluation,
    bool IsPlanComplete,
    PersistedSkillUnlock? NextUnlock);

/// <summary>
/// The plan-aware executor (#228, foundation): walks a persisted #227 plan
/// phase-by-phase and verifies phase completion against actual inventory.
///
/// Phase-complete predicate (per the issue): a phase that grinds <c>N</c> crafts
/// of its recipe yields <c>N × outputStackSize</c> of the recipe's primary
/// output; the phase is complete when on-hand of that output ≥ that threshold.
/// One uniform predicate — agnostic to whether the target was crafted, bought,
/// or dropped from variable output (handles random-output variance *and*
/// externally-supplied ingredients).
///
/// Pure/headless: phase evaluation takes an on-hand map; the UI surface (phase
/// timeline, sourcing toggles, re-plan button) is #228 PR-B.
/// </summary>
public sealed class PlanExecutor
{
    private readonly IReferenceDataService _ref;
    private readonly CrossSkillPlanner _planner;
    private readonly IActiveCharacterService _activeChar;

    public PlanExecutor(
        IReferenceDataService referenceData,
        CrossSkillPlanner planner,
        IActiveCharacterService activeChar)
    {
        _ref = referenceData;
        _planner = planner;
        _activeChar = activeChar;
    }

    /// <summary>
    /// Evaluate one phase: resolve its recipe's primary output, compute the
    /// produced-quantity threshold, and compare against effective on-hand.
    /// </summary>
    public PhaseEvaluation EvaluatePhase(
        PersistedPlanPhase phase,
        IReadOnlyDictionary<string, int> onHandByInternalName)
    {
        if (!_ref.RecipesByInternalName.TryGetValue(phase.RecipeInternalName, out var recipe))
            return new PhaseEvaluation("", 0, 0, IsComplete: false);

        var (outputName, stack) = PrimaryOutput(recipe);
        if (string.IsNullOrEmpty(outputName))
            return new PhaseEvaluation("", 0, 0, IsComplete: false);

        var threshold = phase.PredictedCrafts * Math.Max(1, stack);
        var onHand = onHandByInternalName.TryGetValue(outputName, out var c) ? c : 0;
        return new PhaseEvaluation(outputName, threshold, onHand, onHand >= threshold);
    }

    /// <summary>
    /// The walk state for <paramref name="plan"/> given current inventory:
    /// the current phase, its evaluation, whether the whole plan is satisfied,
    /// and the next skill-source unlock callout (if any).
    /// </summary>
    public PlanWalkState Evaluate(PersistedPlan plan, IReadOnlyDictionary<string, int> onHand)
    {
        if (plan.Phases.Count == 0)
            return new PlanWalkState(0, null, null, IsPlanComplete: true, NextUnlock: null);

        var idx = Math.Clamp(plan.CurrentPhaseIndex, 0, plan.Phases.Count - 1);
        var phase = plan.Phases[idx];
        var eval = EvaluatePhase(phase, onHand);

        // Whole plan done = on the last phase and it's complete.
        var isLast = idx >= plan.Phases.Count - 1;
        var planComplete = isLast && eval.IsComplete;

        // Next unlock = the earliest unlock that fires at or after the next phase's
        // starting level — drives the "Phase N unlocks at Skill L" callout.
        PersistedSkillUnlock? nextUnlock = null;
        if (!isLast)
        {
            var nextLevel = plan.Phases[idx + 1].LevelAtStart;
            nextUnlock = plan.Unlocks
                .Where(u => u.AtLevel >= phase.LevelAtStart && u.AtLevel <= nextLevel)
                .OrderBy(u => u.AtLevel)
                .FirstOrDefault();
        }

        return new PlanWalkState(idx, phase, eval, planComplete, nextUnlock);
    }

    /// <summary>
    /// Advance the cursor if the current phase's predicate has tripped. Returns
    /// true if it moved. Mutates <paramref name="plan"/>'s
    /// <see cref="PersistedPlan.CurrentPhaseIndex"/> in place — the caller persists.
    /// When the predicate has NOT tripped (e.g. random output ran cold) this is a
    /// no-op; the caller decides whether to keep crafting or re-plan.
    /// </summary>
    public bool TryAdvance(PersistedPlan plan, IReadOnlyDictionary<string, int> onHand)
    {
        if (plan.Phases.Count == 0) return false;
        var idx = Math.Clamp(plan.CurrentPhaseIndex, 0, plan.Phases.Count - 1);
        if (idx >= plan.Phases.Count - 1) return false;
        if (!EvaluatePhase(plan.Phases[idx], onHand).IsComplete) return false;
        plan.CurrentPhaseIndex = idx + 1;
        return true;
    }

    /// <summary>
    /// Build a fresh plan for <paramref name="target"/> from the *current* active
    /// character + supplied inventory and sourcing. Returns the persisted form
    /// (cursor at phase 0), or <c>null</c> when there's no active character or no
    /// viable path / umbrella skill.
    /// </summary>
    public PersistedPlan? CreatePlan(
        SkillTarget target,
        SourcingPolicy sourcing,
        IReadOnlyDictionary<string, int> onHand)
    {
        var snapshot = _activeChar.ActiveCharacter;
        if (snapshot is null) return null;

        var plan = _planner.Plan(
            target, ToSkillState(snapshot), ToRecipeHistory(snapshot), onHand, sourcing);
        return plan is null ? null : PersistedPlan.From(plan, target, sourcing);
    }

    /// <summary>
    /// Re-plan from current state, reusing the existing plan's target + sourcing
    /// snapshot — "re-plan from current state" (#228). Cheap; the caller swaps the
    /// persisted plan and resets the cursor.
    /// </summary>
    public PersistedPlan? Replan(PersistedPlan existing, IReadOnlyDictionary<string, int> onHand)
        => CreatePlan(existing.ToSkillTarget(), existing.ToSourcingPolicy(), onHand);

    /// <summary>Project a character export's progression facet into the neutral planner input.</summary>
    public static SkillState ToSkillState(CharacterSnapshot snapshot)
        => new(snapshot.Skills.ToDictionary(
            kv => kv.Key,
            kv => new SkillProgress(
                kv.Value.Level, kv.Value.BonusLevels,
                kv.Value.XpTowardNextLevel, kv.Value.XpNeededForNextLevel),
            StringComparer.Ordinal));

    public static RecipeHistory ToRecipeHistory(CharacterSnapshot snapshot)
        => new(snapshot.RecipeCompletions);

    private (string InternalName, int StackSize) PrimaryOutput(Recipe recipe)
    {
        if (recipe.ResultItems is { } results)
            foreach (var r in results)
                if (_ref.Items.TryGetValue(r.ItemCode, out var item) && !string.IsNullOrEmpty(item.InternalName))
                    return (item.InternalName!, Math.Max(1, r.StackSize));
        if (recipe.ProtoResultItems is { } proto)
            foreach (var r in proto)
                if (_ref.Items.TryGetValue(r.ItemCode, out var item) && !string.IsNullOrEmpty(item.InternalName))
                    return (item.InternalName!, Math.Max(1, r.StackSize));
        return ("", 0);
    }
}
