using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elrond.Domain;
using Elrond.Services;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Elrond.ViewModels;

public sealed partial class SkillAdvisorViewModel : ObservableObject, IDisposable
{
    private readonly SkillAdvisorEngine _engine;
    private readonly LevelingSimulator _simulator;
    private readonly IActiveCharacterService _activeChar;
    private readonly IReferenceDataService _referenceData;
    private readonly ElrondSettings _settings;

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
    private IReadOnlyList<RecipeAnalysis> _filteredRecipes = [];

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

    public DataGridState GridState => _settings.RecipeGrid;

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

    partial void OnShowKnownOnlyChanged(bool value) => ApplyRecipeFilter();
    partial void OnShowFirstTimeOnlyChanged(bool value) => ApplyRecipeFilter();
    partial void OnShowCraftableOnlyChanged(bool value) => ApplyRecipeFilter();
    partial void OnIncludeZeroXpChanged(bool value) => Reanalyze();

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

    // ── Private helpers ──────────────────────────────────────────────────

    private void ReloadSkills()
    {
        var skills = _engine.GetSkillsWithRecipes();

        // If a character is selected, filter to skills the character has
        var active = _activeChar.ActiveCharacter;
        if (active is not null)
        {
            skills = skills.Where(s => active.Skills.ContainsKey(s)).ToList();
        }

        AvailableSkills = new ObservableCollection<string>(skills);

        // Restore last selection
        SelectedSkill = skills.Contains(_settings.LastSkill) ? _settings.LastSkill : skills.FirstOrDefault();
    }

    private void Reanalyze()
    {
        var active = _activeChar.ActiveCharacter;
        if (active is null)
        {
            Analysis = null;
            StatusMessage = "No active character — switch one from the shell header.";
            return;
        }
        if (string.IsNullOrEmpty(SelectedSkill))
        {
            Analysis = null;
            StatusMessage = $"{active.Name} on {active.Server} — select a skill.";
            return;
        }

        Analysis = _engine.Analyze(SelectedSkill, active, IncludeZeroXp, GoalLevel);
        ApplyRecipeFilter();

        if (Analysis is not null)
        {
            StatusMessage = $"{active.Name} on {active.Server} — exported {active.ExportedAt:g}";
        }
        else
        {
            StatusMessage = $"No data for {SelectedSkill} on this character.";
        }
    }

    public void ApplyRecipeFilter()
    {
        if (Analysis is null)
        {
            FilteredRecipes = [];
            return;
        }

        IEnumerable<RecipeAnalysis> filtered = Analysis.Recipes;

        // Existing checkbox filters
        if (ShowKnownOnly) filtered = filtered.Where(r => r.IsKnown);
        if (ShowFirstTimeOnly) filtered = filtered.Where(r => r.FirstTimeBonusAvailable);
        if (ShowCraftableOnly) filtered = filtered.Where(r => r.LevelRequired <= Analysis.CurrentLevel);

        // Per-column filters
        foreach (var col in GridState.Columns)
        {
            if (string.IsNullOrEmpty(col.FilterText)) continue;
            var filter = col.FilterText;
            var key = col.Key;
            filtered = filtered.Where(row =>
                GetCellText(row, key).Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        // Sort
        var sortCol = GridState.Columns.FirstOrDefault(c => c.SortDirection is not null);
        if (sortCol is not null)
        {
            filtered = sortCol.SortDirection == "Ascending"
                ? filtered.OrderBy(r => GetSortValue(r, sortCol.Key))
                : filtered.OrderByDescending(r => GetSortValue(r, sortCol.Key));
        }

        FilteredRecipes = filtered.ToList();
    }

    private static string GetCellText(RecipeAnalysis row, string key) => key switch
    {
        "RecipeName" => row.RecipeName,
        "LevelRequired" => row.LevelRequired.ToString(),
        "BaseXp" => row.BaseXp.ToString(),
        "FirstTimeXp" => row.FirstTimeXp.ToString(),
        "TimesCompleted" => row.TimesCompleted.ToString(),
        "FirstTimeBonusAvailable" => row.FirstTimeBonusAvailable ? "Yes" : "No",
        "EffectiveXp" => row.EffectiveXp.ToString(),
        "CompletionsToLevel" => row.CompletionsToLevel?.ToString() ?? "",
        _ => "",
    };

    private static IComparable GetSortValue(RecipeAnalysis row, string key) => key switch
    {
        "RecipeName" => row.RecipeName,
        "LevelRequired" => row.LevelRequired,
        "BaseXp" => row.BaseXp,
        "FirstTimeXp" => row.FirstTimeXp,
        "TimesCompleted" => row.TimesCompleted,
        "FirstTimeBonusAvailable" => row.FirstTimeBonusAvailable ? 1 : 0,
        "EffectiveXp" => row.EffectiveXp,
        "CompletionsToLevel" => row.CompletionsToLevel ?? int.MaxValue,
        _ => "",
    };

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
    }
}
