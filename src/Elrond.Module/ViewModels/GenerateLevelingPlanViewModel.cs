using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elrond.Services;
using Mithril.Planning;
using Mithril.Shared.Character;
using Mithril.Shared.Modules;

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
    private readonly IActiveCharacterService _activeChar;
    private readonly CrossSkillPlanner _planner;

    // Deferred resolution: resolving the import target at construction would
    // close a DI cycle (→ Celebrimbor's target → IModuleActivator →
    // ShellViewModel → eager ActivateModule → back to an Elrond VM) that MS.DI
    // turns into a silent UI-thread deadlock. Resolve only on the Generate
    // click — exact same pattern as SkillAdvisorViewModel's craft-list accessor.
    private readonly Func<ISavedLevelingPlanImportTarget?>? _importAccessor;

    private LevelingPlan? _preview;
    private bool _userEdited;
    private bool _seeding;

    public GenerateLevelingPlanViewModel(
        IActiveCharacterService activeChar,
        CrossSkillPlanner planner,
        Func<ISavedLevelingPlanImportTarget?>? importAccessor = null)
    {
        _activeChar = activeChar;
        _planner = planner;
        _importAccessor = importAccessor;

        _activeChar.ActiveCharacterChanged += (_, _) => RefreshSnapshot();
        _activeChar.CharacterExportsChanged += (_, _) => RefreshSnapshot();
        RefreshSnapshot();
    }

    // ── Snapshot (embedded initial state) ────────────────────────────────
    [ObservableProperty] private bool _hasActiveCharacter;
    [ObservableProperty] private string _snapshotName = "";
    [ObservableProperty] private string _snapshotServer = "";
    [ObservableProperty] private string _snapshotRelTime = "";

    // ── Target ───────────────────────────────────────────────────────────
    [ObservableProperty] private IReadOnlyList<string> _availableSkills = [];
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
            if (!string.IsNullOrEmpty(skill) && AvailableSkills.Contains(skill))
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
        var snap = _activeChar.ActiveCharacter;
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
        SnapshotRelTime = HumanizeAge(DateTimeOffset.Now - snap.ExportedAt);
        AvailableSkills = snap.Skills.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        Recompute();
    }

    private void Recompute()
    {
        var snap = _activeChar.ActiveCharacter;
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
        var snap = _activeChar.ActiveCharacter;
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

    private static string HumanizeAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
