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
/// The plan-aware executor (#228, foundation): walks a <see cref="SavedLevelingPlan"/>
/// phase-by-phase and verifies phase completion against actual inventory.
///
/// Phase-complete predicate (per the issue): a phase that grinds <c>N</c> crafts
/// of its recipe yields <c>N × outputStackSize</c> of the recipe's primary
/// output; the phase is complete when on-hand of that output ≥ that threshold.
/// One uniform predicate — agnostic to whether the target was crafted, bought,
/// or dropped from variable output (handles random-output variance *and*
/// externally-supplied ingredients).
///
/// Pure/headless: phase evaluation takes an on-hand map; the plan-manager UI
/// (list/select, phase timeline, sourcing toggles, stale-refresh offer) is
/// #228 PR-B. Plans are arbitrary-input: <see cref="CreatePlan"/> takes explicit
/// state, with active-character / weak-ref convenience overloads.
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

    // ── Phase walking ────────────────────────────────────────────────────

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
    /// current phase, its evaluation, whether the whole plan is satisfied, and
    /// the next skill-source unlock callout (if any).
    /// </summary>
    public PlanWalkState Evaluate(SavedLevelingPlan plan, IReadOnlyDictionary<string, int> onHand)
    {
        if (plan.Phases.Count == 0)
            return new PlanWalkState(0, null, null, IsPlanComplete: true, NextUnlock: null);

        var idx = Math.Clamp(plan.CurrentPhaseIndex, 0, plan.Phases.Count - 1);
        var phase = plan.Phases[idx];
        var eval = EvaluatePhase(phase, onHand);

        var isLast = idx >= plan.Phases.Count - 1;
        var planComplete = isLast && eval.IsComplete;

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
    /// <see cref="SavedLevelingPlan.CurrentPhaseIndex"/> in place — the caller
    /// persists. When the predicate has NOT tripped (e.g. random output ran cold)
    /// this is a no-op; the caller decides whether to keep crafting or re-plan.
    /// </summary>
    public bool TryAdvance(SavedLevelingPlan plan, IReadOnlyDictionary<string, int> onHand)
    {
        if (plan.Phases.Count == 0) return false;
        var idx = Math.Clamp(plan.CurrentPhaseIndex, 0, plan.Phases.Count - 1);
        if (idx >= plan.Phases.Count - 1) return false;
        if (!EvaluatePhase(plan.Phases[idx], onHand).IsComplete) return false;
        plan.CurrentPhaseIndex = idx + 1;
        return true;
    }

    // ── Plan creation (arbitrary input) ──────────────────────────────────

    /// <summary>
    /// Build a saved plan from <b>explicit</b> state — the general, arbitrary-input
    /// entry point (a hypothetical, an alt, a UI-edited what-if). Returns
    /// <c>null</c> on no viable path / umbrella skill.
    /// </summary>
    public SavedLevelingPlan? CreatePlan(
        SkillTarget target,
        SkillState state,
        RecipeHistory history,
        IReadOnlyDictionary<string, int> onHand,
        SourcingPolicy sourcing,
        PlanCharacterRef? character)
    {
        var plan = _planner.Plan(target, state, history, onHand, sourcing);
        return plan is null ? null : SavedLevelingPlan.From(plan, target, state, history, sourcing, character);
    }

    /// <summary>
    /// Convenience: plan for the active character, embedding its full state and a
    /// weak ref to it. <c>null</c> if no active character / no viable path.
    /// </summary>
    public SavedLevelingPlan? CreatePlanForActiveCharacter(
        SkillTarget target,
        SourcingPolicy sourcing,
        IReadOnlyDictionary<string, int> onHand)
    {
        var snap = _activeChar.ActiveCharacter;
        if (snap is null) return null;
        return CreatePlan(target, ToSkillState(snap), ToRecipeHistory(snap), onHand,
            sourcing, PlanCharacterRef.FromSnapshot(snap));
    }

    /// <summary>
    /// Re-plan an existing saved plan, reusing its target + sourcing. If its weak
    /// character ref resolves to a live export, the embedded initial state is
    /// <b>refreshed</b> from that export (intake newly learned recipes / skill-ups)
    /// — "refresh &amp; re-plan"; otherwise it re-plans from the embedded state.
    /// Same Id / CreatedAt (a refresh of the same logical plan), cursor reset.
    /// </summary>
    public SavedLevelingPlan? Replan(SavedLevelingPlan existing, IReadOnlyDictionary<string, int> onHand)
    {
        var live = ResolveLiveSnapshot(existing.Character);
        SkillState state;
        RecipeHistory history;
        PlanCharacterRef? character;
        if (live is not null)
        {
            state = ToSkillState(live);
            history = ToRecipeHistory(live);
            character = PlanCharacterRef.FromSnapshot(live);
        }
        else
        {
            state = existing.ToInitialSkillState();
            history = existing.ToInitialRecipeHistory();
            character = existing.Character;
        }

        var plan = _planner.Plan(existing.ToSkillTarget(), state, history, onHand, existing.ToSourcingPolicy());
        if (plan is null) return null;

        var refreshed = SavedLevelingPlan.From(
            plan, existing.ToSkillTarget(), state, history, existing.ToSourcingPolicy(), character);
        refreshed.Id = existing.Id;
        refreshed.CreatedAt = existing.CreatedAt;
        return refreshed;
    }

    /// <summary>Newest live export matching the weak ref's identity, or null.</summary>
    public CharacterSnapshot? ResolveLiveSnapshot(PlanCharacterRef? r)
        => r is null
            ? null
            : _activeChar.Characters.FirstOrDefault(c =>
                string.Equals(c.Name, r.Name, StringComparison.Ordinal)
                && string.Equals(c.Server, r.Server, StringComparison.Ordinal));

    /// <summary>Project a character export's progression facet into the neutral planner input.</summary>
    public static SkillState ToSkillState(CharacterSnapshot snapshot)
        => new(snapshot.Skills.ToDictionary(
            kv => kv.Key,
            kv => new SkillProgress(
                kv.Value.Level, kv.Value.BonusLevels,
                kv.Value.XpTowardNextLevel, kv.Value.XpNeededForNextLevel),
            StringComparer.Ordinal));

    public static RecipeHistory ToRecipeHistory(CharacterSnapshot snapshot)
        => new(new Dictionary<string, int>(snapshot.RecipeCompletions, StringComparer.Ordinal));

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
