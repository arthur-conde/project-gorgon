using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elrond.Domain;
using Elrond.Services;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;

namespace Elrond.ViewModels;

public sealed record SortOption(string Label, string Key);

public sealed partial class SkillAdvisorViewModel : ObservableObject, IDisposable
{
    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new("Recipe", "RecipeName"),
        new("Lvl Req", "LevelRequired"),
        new("Eff. XP", "EffectiveXp"),
        new("To Level", "CompletionsToLevel"),
    ];

    private readonly SkillAdvisorEngine _engine;
    private readonly LevelingSimulator _simulator;
    private readonly IActiveCharacterService _activeChar;
    private readonly IReferenceDataService _referenceData;
    private readonly ElrondSettings _settings;

    private readonly ObservableCollection<RecipeAnalysis> _recipes = [];

    public ICollectionView RecipesView { get; }

    public SkillAdvisorViewModel(
        SkillAdvisorEngine engine,
        LevelingSimulator simulator,
        IActiveCharacterService characterData,
        IReferenceDataService referenceData,
        ElrondSettings settings)
    {
        _engine = engine;
        _simulator = simulator;
        _activeChar = characterData;
        _referenceData = referenceData;
        _settings = settings;

        _goalLevel = settings.LastGoalLevel;
        _sortKey = settings.SortKey;
        _sortDescending = settings.SortDescending;
        _viewMode = settings.ViewMode;

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_recipes);
        view.Filter = item => MatchesFilter((RecipeAnalysis)item);
        view.CurrentChanged += OnCurrentRecipeChanged;
        RecipesView = view;
        ApplySortDescriptions();

        _activeChar.ActiveCharacterChanged += OnActiveCharacterChanged;
        _activeChar.CharacterExportsChanged += OnActiveCharacterChanged;
        _referenceData.FileUpdated += OnReferenceUpdated;

        ReloadSkills();
    }

    // ── Observable properties ────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<string> _availableSkills = [];

    [ObservableProperty]
    private string? _selectedSkill;

    [ObservableProperty]
    private SkillAnalysis? _analysis;

    [ObservableProperty]
    private RecipeAnalysis? _selectedRecipe;

    [ObservableProperty]
    private bool _showKnownOnly = true;

    [ObservableProperty]
    private bool _showFirstTimeOnly;

    [ObservableProperty]
    private bool _showCraftableOnly = true;

    [ObservableProperty]
    private bool _includeZeroXp;

    [ObservableProperty]
    private string _statusMessage = "Select a character export to begin.";

    [ObservableProperty]
    private int? _goalLevel;

    [ObservableProperty]
    private SimulationResult? _simulationResult;

    [ObservableProperty]
    private string _sortKey = "EffectiveXp";

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    private string _viewMode = "Rows";

    // ── Property change handlers ─────────────────────────────────────────

    partial void OnSelectedSkillChanged(string? value)
    {
        if (value is not null)
            _settings.LastSkill = value;
        Reanalyze();
    }

    partial void OnGoalLevelChanged(int? value)
    {
        _settings.LastGoalLevel = value;
        SimulationResult = null;
        Reanalyze();
    }

    partial void OnShowKnownOnlyChanged(bool value) => RecipesView.Refresh();
    partial void OnShowFirstTimeOnlyChanged(bool value) => RecipesView.Refresh();
    partial void OnShowCraftableOnlyChanged(bool value) => RecipesView.Refresh();
    partial void OnIncludeZeroXpChanged(bool value) => Reanalyze();

    partial void OnSortKeyChanged(string value)
    {
        _settings.SortKey = value;
        ApplySortDescriptions();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        _settings.SortDescending = value;
        ApplySortDescriptions();
    }

    partial void OnViewModeChanged(string value)
    {
        _settings.ViewMode = value;
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void RefreshCharacterData()
    {
        _activeChar.Refresh();
    }

    [RelayCommand]
    private void Simulate()
    {
        var active = _activeChar.ActiveCharacter;
        if (active is null || string.IsNullOrEmpty(SelectedSkill) || GoalLevel is not { } goal)
        {
            SimulationResult = null;
            return;
        }

        SimulationResult = _simulator.Simulate(SelectedSkill, active, goal);
    }

    [RelayCommand]
    private void ToggleSort(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            // Numeric metrics read top-down (largest first); names read alphabetically.
            SortDescending = key != "RecipeName";
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private void ReloadSkills()
    {
        var skills = _engine.GetSkillsWithRecipes();

        var active = _activeChar.ActiveCharacter;
        if (active is not null)
        {
            skills = skills.Where(s => active.Skills.ContainsKey(s)).ToList();
        }

        AvailableSkills = new ObservableCollection<string>(skills);
        SelectedSkill = skills.Contains(_settings.LastSkill) ? _settings.LastSkill : skills.FirstOrDefault();
    }

    private void Reanalyze()
    {
        var active = _activeChar.ActiveCharacter;
        if (active is null)
        {
            Analysis = null;
            _recipes.Clear();
            StatusMessage = "No active character — switch one from the shell header.";
            return;
        }
        if (string.IsNullOrEmpty(SelectedSkill))
        {
            Analysis = null;
            _recipes.Clear();
            StatusMessage = $"{active.Name} on {active.Server} — select a skill.";
            return;
        }

        Analysis = _engine.Analyze(SelectedSkill, active, IncludeZeroXp, GoalLevel);

        _recipes.Clear();
        if (Analysis is not null)
        {
            foreach (var r in Analysis.Recipes) _recipes.Add(r);
            RecipesView.MoveCurrentToFirst();
            StatusMessage = $"{active.Name} on {active.Server} — exported {active.ExportedAt:g}";
        }
        else
        {
            StatusMessage = $"No data for {SelectedSkill} on this character.";
        }
    }

    private bool MatchesFilter(RecipeAnalysis row)
    {
        if (Analysis is null) return false;
        if (ShowKnownOnly && !row.IsKnown) return false;
        if (ShowFirstTimeOnly && !row.FirstTimeBonusAvailable) return false;
        if (ShowCraftableOnly && row.LevelRequired > Analysis.CurrentLevel) return false;
        return true;
    }

    private void ApplySortDescriptions()
    {
        var dir = SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        RecipesView.SortDescriptions.Clear();
        RecipesView.SortDescriptions.Add(new SortDescription(SortKey, dir));
    }

    private void OnCurrentRecipeChanged(object? sender, EventArgs e)
    {
        SelectedRecipe = RecipesView.CurrentItem as RecipeAnalysis;
    }

    private void OnActiveCharacterChanged(object? sender, EventArgs e)
    {
        ReloadSkills();
        Reanalyze();
    }

    private void OnReferenceUpdated(object? sender, string key)
    {
        if (key is "recipes" or "skills" or "xptables")
        {
            ReloadSkills();
            Reanalyze();
        }
    }

    public void Dispose()
    {
        _activeChar.ActiveCharacterChanged -= OnActiveCharacterChanged;
        _activeChar.CharacterExportsChanged -= OnActiveCharacterChanged;
        _referenceData.FileUpdated -= OnReferenceUpdated;
        RecipesView.CurrentChanged -= OnCurrentRecipeChanged;
    }
}
