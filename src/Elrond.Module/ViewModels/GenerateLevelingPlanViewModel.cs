using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elrond.Services;
using Mithril.GameReports;
using Mithril.Planning;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;

namespace Elrond.ViewModels;

/// <summary>
/// "Generate leveling plan" (#228 PR-B/B2). Elrond authors the plan — it has
/// the skill selector, goal and the live progression snapshot — using the
/// converged shared <see cref="CrossSkillPlanner"/>, then hands a
/// <see cref="SavedLevelingPlan"/> off to Celebrimbor (the executor) via the
/// in-process <see cref="ISavedLevelingPlanImportTarget"/>. Charter-correct:
/// Elrond generates, Celebrimbor receives/walks.
///
/// <para>Four states mirror the spec: empty (no skill), preview (skill+goal),
/// no active character, and goal ≤ current (no-op). Advanced inputs (sourcing
/// policy, asserted unlocks) are deferred — the planner defaults apply.</para>
/// </summary>
public sealed partial class GenerateLevelingPlanViewModel : ObservableObject
{
    private readonly LiveProgressionAdapter _progression;
    private readonly CrossSkillPlanner _planner;

    private CharacterSnapshot? ActiveCharacterSnapshot => _progression.GetMergedSnapshot();

    // Deferred resolution: resolving the import target at construction would
    // close a DI cycle (→ Celebrimbor's target → IModuleActivator →
    // ShellViewModel → eager ActivateModule → back to an Elrond VM) that MS.DI
    // turns into a silent UI-thread deadlock. Resolve only on the Generate
    // click — exact same pattern as SkillAdvisorViewModel's craft-list accessor.
    private readonly Func<ISavedLevelingPlanImportTarget?>? _importAccessor;

    private LevelingPlan? _preview;
    private bool _userEdited;
    private bool _seeding;

    private readonly IReferenceDataService _ref;

    public GenerateLevelingPlanViewModel(
        LiveProgressionAdapter progression,
        CrossSkillPlanner planner,
        IReferenceDataService referenceData,
        Func<ISavedLevelingPlanImportTarget?>? importAccessor = null)
    {
        _progression = progression;
        _planner = planner;
        _ref = referenceData;
        _importAccessor = importAccessor;

        _progression.Changed += OnProgressionChanged;
        RefreshSnapshot();
    }

    private void OnProgressionChanged() => RefreshSnapshot();

    // ── Snapshot (embedded initial state) ────────────────────────────────
    [ObservableProperty] private bool _hasActiveCharacter;
    [ObservableProperty] private string _snapshotName = "";
    [ObservableProperty] private string _snapshotServer = "";
    [ObservableProperty] private string _snapshotRelTime = "";

    // ── Target ───────────────────────────────────────────────────────────
    /// <summary>A pickable skill: the id-shaped <see cref="Key"/> is the model
    /// value; <see cref="DisplayName"/> is what the user sees/types.</summary>
    public readonly record struct SkillChoice(string Key, string DisplayName);

    [ObservableProperty] private IReadOnlyList<SkillChoice> _availableSkills = [];
    [ObservableProperty] private string? _selectedSkill;
    [ObservableProperty] private int? _currentLevel;
    [ObservableProperty] private int? _goalLevel;
    [ObservableProperty] private bool _goalInvalid;

    // ── Preview ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private bool _alreadyAtGoal;
    [ObservableProperty] private int _previewPhases;
    [ObservableProperty] private int _previewTotalCrafts;
    [ObservableProperty] private int _previewStartLevel;
    [ObservableProperty] private int _previewGoalLevel;
    [ObservableProperty] private int _previewUnlocks;

    public bool CanGenerate => _preview is { } p && !p.IsComplete && HasActiveCharacter;

    /// <summary>Raised after a plan was generated and handed off (UI may navigate away).</summary>
    public event EventHandler? PlanHandedOff;

    /// <summary>
    /// Prefill skill/goal from the advisor's current selection — continuity for
    /// a user who was just analysing that skill. Ignored once the user has
    /// edited the generate inputs themselves (seed, don't clobber).
    /// </summary>
    public void SeedFromAdvisor(string? skill, int? goalLevel)
    {
        if (_userEdited) return;
        _seeding = true;
        try
        {
            if (!string.IsNullOrEmpty(skill) && AvailableSkills.Any(c => c.Key == skill))
                SelectedSkill = skill;
            if (goalLevel is { } g)
                GoalLevel = g;
        }
        finally { _seeding = false; }
        Recompute();
    }

    partial void OnSelectedSkillChanged(string? value)
    {
        if (!_seeding) _userEdited = true;
        Recompute();
    }

    partial void OnGoalLevelChanged(int? value)
    {
        if (!_seeding) _userEdited = true;
        Recompute();
    }

    private void RefreshSnapshot()
    {
        var snap = ActiveCharacterSnapshot;
        HasActiveCharacter = snap is not null;
        if (snap is null)
        {
            SnapshotName = SnapshotServer = SnapshotRelTime = "";
            AvailableSkills = [];
            Recompute();
            return;
        }
        SnapshotName = snap.Name;
        SnapshotServer = snap.Server;
        SnapshotRelTime = FormatSnapshotAge(snap);
        // Resolve id-shaped keys → human display names (convention); the model
        // (SelectedSkill / SkillTarget) still carries the key. Order by display.
        AvailableSkills = snap.Skills.Keys
            .Select(k => new SkillChoice(
                k, _ref.Skills.TryGetValue(k, out var e) ? e.DisplayName : k))
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Recompute();
    }

    private void Recompute()
    {
        var snap = ActiveCharacterSnapshot;
        _preview = null;
        HasPreview = false;
        AlreadyAtGoal = false;
        CurrentLevel = null;
        GoalInvalid = false;

        if (snap is null || string.IsNullOrEmpty(SelectedSkill))
        {
            NotifyGenerate();
            return;
        }

        CurrentLevel = snap.Skills.TryGetValue(SelectedSkill, out var cs) ? cs.Level : null;

        if (GoalLevel is not { } goal)
        {
            NotifyGenerate();
            return;
        }

        if (CurrentLevel is { } cur && goal <= cur)
        {
            GoalInvalid = true;
            AlreadyAtGoal = true;
            NotifyGenerate();
            return;
        }

        var plan = _planner.Plan(
            new SkillTarget(SelectedSkill, goal),
            SnapshotPlanInput.ToSkillState(snap),
            SnapshotPlanInput.ToRecipeHistory(snap));

        if (plan is null || plan.IsComplete)
        {
            NotifyGenerate();
            return;
        }

        _preview = plan;
        PreviewPhases = plan.Phases.Count;
        PreviewTotalCrafts = plan.TotalCrafts;
        PreviewStartLevel = plan.StartLevel;
        PreviewGoalLevel = plan.GoalLevel;
        PreviewUnlocks = plan.Unlocks.Count;
        HasPreview = true;
        NotifyGenerate();
    }

    private void NotifyGenerate()
    {
        OnPropertyChanged(nameof(CanGenerate));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Generate()
    {
        var snap = ActiveCharacterSnapshot;
        if (snap is null || _preview is not { } plan || string.IsNullOrEmpty(SelectedSkill)
            || GoalLevel is not { } goal)
            return;

        var target = new SkillTarget(SelectedSkill, goal);
        var saved = SavedLevelingPlan.From(
            plan,
            target,
            SnapshotPlanInput.ToSkillState(snap),
            SnapshotPlanInput.ToRecipeHistory(snap),
            SourcingPolicy.CraftEverything,
            SnapshotPlanInput.ToCharacterRef(snap));

        var json = JsonSerializer.Serialize(saved, SavedLevelingPlanJsonContext.Default.SavedLevelingPlan);
        _importAccessor?.Invoke()?.ImportPlan(json, "Elrond");
        PlanHandedOff?.Invoke(this, EventArgs.Empty);
    }

    private string FormatSnapshotAge(CharacterSnapshot snap)
    {
        var reference = _progression.LastDataSource switch
        {
            ProgressionDataSource.ExportOnly => snap.ExportedAt,
            ProgressionDataSource.LiveOnly => _progression.LiveMeasuredAt ?? snap.ExportedAt,
            ProgressionDataSource.Merged => _progression.LiveMeasuredAt ?? snap.ExportedAt,
            _ => snap.ExportedAt,
        };
        return HumanizeAge(DateTimeOffset.Now - reference);
    }

    private static string HumanizeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
