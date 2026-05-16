using System.Collections.ObjectModel;
using Celebrimbor.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Planning;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;

namespace Celebrimbor.ViewModels;

/// <summary>
/// Walks one <see cref="SavedLevelingPlan"/> (#228 PR-B/B1, frames 03/06/07):
/// phase rail with unlock callouts, the inventory-vs-threshold completion
/// predicate, per-ingredient sourcing toggles, send-phase-to-craft-list, the
/// stale-refresh banner and the plan-complete terminal.
///
/// <para>Inventory-driven only: completion is <see cref="PlanExecutor"/>'s
/// on-hand ≥ produced-threshold predicate. The craft-count-telemetry frames
/// (drift "output ran short", 2 s auto-advance countdown, walked/xp-banked
/// stats) are deferred — they need a Player.log craft tracker that the PR-A
/// foundation doesn't provide. Phase advance here is therefore an explicit
/// user action, not a timed auto-advance.</para>
/// </summary>
public sealed partial class PlanWalkerViewModel : ObservableObject
{
    private readonly LevelingPlanStore _store;
    private readonly PlanExecutor _executor;
    private readonly OnHandInventoryQuery _onHand;
    private readonly IReferenceDataService _ref;
    private readonly ICraftListImportTarget _craftList;

    private SavedLevelingPlan? _plan;
    private bool _staleDismissed;

    public PlanWalkerViewModel(
        LevelingPlanStore store,
        PlanExecutor executor,
        OnHandInventoryQuery onHand,
        IReferenceDataService referenceData,
        ICraftListImportTarget craftList)
    {
        _store = store;
        _executor = executor;
        _onHand = onHand;
        _ref = referenceData;
        _craftList = craftList;
    }

    /// <summary>Raised when the user leaves the walker (back to the library).</summary>
    public event EventHandler? BackRequested;

    /// <summary>Raised when the plan was mutated (advance / re-plan) so the manager re-reads.</summary>
    public event EventHandler? PlanChanged;

    public void Load(SavedLevelingPlan plan)
    {
        _plan = plan;
        _staleDismissed = false;
        Recompute();
    }

    // ── Header ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _skill = "";
    [ObservableProperty] private string _characterLabel = "";
    [ObservableProperty] private string _goalLabel = "";
    [ObservableProperty] private string _phaseCountLabel = "";

    // ── Rail ─────────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<PlanRailItemViewModel> _rail = [];

    // ── Current-phase detail ─────────────────────────────────────────────
    [ObservableProperty] private bool _hasCurrentPhase;
    [ObservableProperty] private string _detailTitle = "";
    [ObservableProperty] private string _detailLevels = "";
    [ObservableProperty] private int _detailPredictedCrafts;
    [ObservableProperty] private int _detailXpPerCraft;
    [ObservableProperty] private int _detailReuseXp;
    [ObservableProperty] private bool _detailUsesFirstTimeBonus;

    // ── Predicate ────────────────────────────────────────────────────────
    [ObservableProperty] private int _predicateOnHand;
    [ObservableProperty] private int _predicateThreshold;
    [ObservableProperty] private double _predicateFraction;
    [ObservableProperty] private bool _predicateComplete;

    // ── Sourcing ─────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<SourcingRowViewModel> _sourcingRows = [];

    // ── Footer ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _positionText = "";
    [ObservableProperty] private string _remainingText = "";

    // ── Terminal / stale ─────────────────────────────────────────────────
    [ObservableProperty] private bool _isPlanComplete;
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private string _staleSummary = "";
    [ObservableProperty] private string _createdAgeText = "";

    public bool CanAdvance => _plan is not null && !IsPlanComplete && PredicateComplete;

    private void Recompute()
    {
        if (_plan is null) return;
        var plan = _plan;

        var onHand = _onHand.QueryActiveCharacter().Counts;
        var walk = _executor.Evaluate(plan, onHand);
        var live = _executor.ResolveLiveSnapshot(plan.Character);

        Skill = plan.Skill;
        CharacterLabel = plan.Character is { } c ? $"{c.Name} @ {c.Server}" : "hypothetical";
        GoalLabel = $"Goal L{plan.GoalLevel}";
        var phaseCount = plan.Phases.Count;
        IsPlanComplete = walk.IsPlanComplete || plan.CurrentPhaseIndex >= phaseCount;

        // Auto-finalize: when the last phase's predicate is satisfied the plan is
        // complete, but the cursor only advances *between* phases (TryAdvance is a
        // no-op on the last). Persist the terminal sentinel (cursor == Phases.Count)
        // so the manager's cursor-based Done state matches reality. Idempotent.
        if (IsPlanComplete && phaseCount > 0 && plan.CurrentPhaseIndex < phaseCount)
        {
            plan.CurrentPhaseIndex = phaseCount;
            _store.Upsert(plan);
            PlanChanged?.Invoke(this, EventArgs.Empty);
        }

        var completed = Math.Min(Math.Max(plan.CurrentPhaseIndex, 0), phaseCount);
        PhaseCountLabel = IsPlanComplete ? $"{phaseCount} / {phaseCount} complete" : $"{phaseCount} total";

        IsStale = !_staleDismissed && live is not null && plan.IsInitialStateStaleAgainst(live);
        StaleSummary = IsStale ? BuildStaleSummary(plan, live!) : "";
        CreatedAgeText = HumanizeAge(DateTimeOffset.Now - plan.CreatedAt);

        BuildRail(plan, walk, live);

        if (!IsPlanComplete && walk.CurrentPhase is { } cur)
        {
            HasCurrentPhase = true;
            DetailTitle = cur.RecipeName;
            DetailLevels = $"L{cur.LevelAtStart} → L{cur.LevelAtEnd}";
            DetailPredictedCrafts = cur.PredictedCrafts;
            DetailXpPerCraft = cur.XpPerCraft;
            DetailReuseXp = cur.IntermediateReuseXpPerCraft;
            DetailUsesFirstTimeBonus = cur.UsesFirstTimeBonus;

            var eval = walk.CurrentEvaluation;
            PredicateOnHand = eval?.OnHand ?? 0;
            PredicateThreshold = eval?.Threshold ?? 0;
            PredicateFraction = PredicateThreshold <= 0
                ? 0d : Math.Clamp((double)PredicateOnHand / PredicateThreshold, 0d, 1d);
            PredicateComplete = eval?.IsComplete ?? false;

            BuildSourcing(plan, cur);

            var remainingCrafts = plan.Phases.Skip(plan.CurrentPhaseIndex).Sum(p => p.PredictedCrafts);
            PositionText = $"Phase {plan.CurrentPhaseIndex + 1} of {phaseCount}";
            RemainingText = $"Σ {remainingCrafts} predicted crafts remaining";
        }
        else
        {
            HasCurrentPhase = false;
            SourcingRows = [];
            PositionText = $"Phase {completed} of {phaseCount}";
            RemainingText = "plan terminal";
        }

        AdvancePhaseCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAdvance));
    }

    private void BuildRail(SavedLevelingPlan plan, PlanWalkState walk, Mithril.Shared.Character.CharacterSnapshot? live)
    {
        var rail = new ObservableCollection<PlanRailItemViewModel>();
        var unlocks = plan.Unlocks.OrderBy(u => u.AtLevel).ToList();

        for (var i = 0; i < plan.Phases.Count; i++)
        {
            var p = plan.Phases[i];
            var state = i < plan.CurrentPhaseIndex || IsPlanComplete ? PhaseRailState.Done
                : i == plan.CurrentPhaseIndex ? PhaseRailState.Current
                : PhaseRailState.Upcoming;

            var isCurrent = state == PhaseRailState.Current && walk.CurrentEvaluation is { } e;
            rail.Add(PlanRailItemViewModel.Phase(
                state, p.RecipeName, p.LevelAtStart, p.LevelAtEnd,
                p.PredictedCrafts, p.XpPerCraft, p.UsesFirstTimeBonus,
                showMiniProgress: state == PhaseRailState.Current,
                onHand: isCurrent ? walk.CurrentEvaluation!.OnHand : 0,
                threshold: isCurrent ? walk.CurrentEvaluation!.Threshold : 0));

            // Unlock callouts belong to the phase whose level band covers them
            // (displayed as the interstitial leading into the next phase).
            foreach (var u in unlocks.Where(u =>
                         u.AtLevel >= p.LevelAtStart &&
                         (i == plan.Phases.Count - 1 || u.AtLevel < plan.Phases[i + 1].LevelAtStart)))
            {
                var alreadyKnown = live is not null
                    && !string.IsNullOrEmpty(u.RecipeInternalName)
                    && live.RecipeCompletions.ContainsKey(u.RecipeInternalName);
                rail.Add(PlanRailItemViewModel.Unlock(
                    u.AtLevel, u.RecipeName, u.XpPerCraftAtUnlock, u.Reason, alreadyKnown));
            }
        }

        Rail = rail;
    }

    private void BuildSourcing(SavedLevelingPlan plan, PersistedPlanPhase phase)
    {
        var rows = new ObservableCollection<SourcingRowViewModel>();
        if (_ref.RecipesByInternalName.TryGetValue(phase.RecipeInternalName, out var recipe))
        {
            var policy = plan.ToSourcingPolicy();
            foreach (var ing in recipe.Ingredients.OfType<RecipeItemIngredient>())
            {
                if (!_ref.Items.TryGetValue(ing.ItemCode, out var item)) continue;
                if (string.IsNullOrEmpty(item.InternalName)) continue;
                rows.Add(new SourcingRowViewModel(
                    item.InternalName!,
                    item.Name ?? item.InternalName!,
                    Math.Max(1, ing.StackSize) * phase.PredictedCrafts,
                    policy.For(item.InternalName!),
                    OnSourcingChanged));
            }
        }
        SourcingRows = rows;
    }

    private void OnSourcingChanged(string itemInternalName, SourcingMode mode)
    {
        if (_plan is null) return;
        _plan.Sourcing = _plan.Sourcing
            .Where(s => !string.Equals(s.ItemInternalName, itemInternalName, StringComparison.Ordinal))
            .Append(new PersistedSourcingEntry { ItemInternalName = itemInternalName, Mode = mode })
            .ToList();
        _store.Upsert(_plan);
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void SendPhaseToCraftList()
    {
        if (_plan is null) return;
        var walk = _executor.Evaluate(_plan, _onHand.QueryActiveCharacter().Counts);
        if (walk.CurrentPhase is not { } cur || string.IsNullOrEmpty(cur.RecipeInternalName)) return;
        _craftList.ImportRecipes(
            [new CraftListImportEntry(cur.RecipeInternalName, Math.Max(1, cur.PredictedCrafts))],
            $"Leveling plan · {_plan.Skill}");
    }

    /// <summary>
    /// Advance past a satisfied <i>non-last</i> phase. The last phase needs no
    /// click — <see cref="Recompute"/> auto-finalizes the terminal sentinel when
    /// its predicate trips. (No telemetry auto-advance; this stays explicit.)
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAdvance))]
    private void AdvancePhase()
    {
        if (_plan is null) return;
        if (!_executor.TryAdvance(_plan, _onHand.QueryActiveCharacter().Counts)) return;
        _store.Upsert(_plan);
        Recompute();
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Replan()
    {
        if (_plan is null) return;
        var refreshed = _executor.Replan(_plan, _onHand.QueryActiveCharacter().Counts);
        if (refreshed is null) return;
        _store.Upsert(refreshed);
        _plan = refreshed;
        _staleDismissed = false;
        Recompute();
        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stale banner primary action — identical to <see cref="Replan"/>:
    /// <see cref="PlanExecutor.Replan"/> already refreshes the embedded state from
    /// the live export when the weak character ref resolves.</summary>
    [RelayCommand]
    private void RefreshAndReplan() => Replan();

    /// <summary>"Walk anyway" — dismiss the stale warning for this session.</summary>
    [RelayCommand]
    private void DismissStale()
    {
        _staleDismissed = true;
        Recompute();
    }

    [RelayCommand]
    private void BackToLibrary() => BackRequested?.Invoke(this, EventArgs.Empty);

    private static string BuildStaleSummary(SavedLevelingPlan plan, Mithril.Shared.Character.CharacterSnapshot live)
    {
        var newRecipes = live.RecipeCompletions.Keys
            .Count(k => !plan.InitialRecipeCompletions.ContainsKey(k));
        var skillNote = plan.InitialSkills.TryGetValue(plan.Skill, out var was)
            && live.Skills.TryGetValue(plan.Skill, out var now) && now.Level != was.Level
            ? $"{plan.Skill} L{was.Level} → L{now.Level}"
            : $"{plan.Skill} baseline unchanged";
        return newRecipes > 0
            ? $"{skillNote} · {newRecipes} new recipe(s) since this plan was generated"
            : $"{skillNote} · progression changed since this plan was generated";
    }

    private static string HumanizeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
