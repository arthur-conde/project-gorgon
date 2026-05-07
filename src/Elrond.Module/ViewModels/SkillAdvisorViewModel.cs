using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elrond.Domain;
using Elrond.Services;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Filtering;
using Mithril.Shared.Wpf.Sorting;

namespace Elrond.ViewModels;

public sealed partial class SkillAdvisorViewModel
    : ObservableObject,
      ISortableViewModel<RecipeAnalysis>,
      IFilterableViewModel<RecipeAnalysis>,
      IDisposable
{
    public IReadOnlyList<SortKey<RecipeAnalysis>> AvailableSortKeys { get; } =
    [
        new("RecipeName",         "Recipe",     "RecipeName"),
        new("LevelRequired",      "Lvl Req",    "LevelRequired"),
        new("EffectiveXp",        "Eff. XP",    "EffectiveXp",        DefaultDescending: true),
        new("Complexity",         "Complexity", "Complexity"),
        new("Efficiency",         "Efficiency", "Efficiency",         DefaultDescending: true),
        new("CompletionsToLevel", "To Level",   "CompletionsToLevel", DefaultDescending: true),
    ];

    public ObservableCollection<ActiveSortKey<RecipeAnalysis>> ActiveSortKeys { get; } = [];

    public IReadOnlyList<FilterPredicate<RecipeAnalysis>> AvailableFilters { get; }

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
        _viewMode = settings.ViewMode;

        // Filters declared here so each closure captures `this` and reads live state
        // (e.g. CraftableOnly compares against the current Analysis at predicate-call time).
        // Labels read from the user's perspective: toggling on does what the label says.
        AvailableFilters =
        [
            // Inverted: predicate r => r.IsKnown applies when IsActive=false; toggling ON reveals unknowns.
            new("ShowUnknown",        "Show unknown",          r => r.IsKnown,                inverted: true,  isActive: false),
            new("FirstTimeBonusOnly", "First Time Bonus Only", r => r.FirstTimeBonusAvailable, inverted: false, isActive: false),
            new("CraftableOnly",      "Craftable only",        r => Analysis is { } a && r.LevelRequired <= a.CurrentLevel, inverted: false, isActive: true),
            new("HideZeroXp",         "Hide 0 XP",             r => r.EffectiveXp > 0,         inverted: false, isActive: true),
        ];

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_recipes);
        view.Filter = item => MatchesActiveFilters((RecipeAnalysis)item);
        view.CurrentChanged += OnCurrentRecipeChanged;
        RecipesView = view;

        HydrateSortKeysFromSettings();
        ActiveSortKeys.CollectionChanged += OnActiveSortKeysChanged;
        foreach (var k in ActiveSortKeys) k.PropertyChanged += OnActiveSortKeyPropertyChanged;
        ApplySortDescriptions();

        HydrateFiltersFromSettings();
        foreach (var f in AvailableFilters) f.PropertyChanged += OnFilterPropertyChanged;

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
    private string _statusMessage = "Select a character export to begin.";

    [ObservableProperty]
    private int? _goalLevel;

    [ObservableProperty]
    private SimulationResult? _simulationResult;

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

    partial void OnAnalysisChanged(SkillAnalysis? value)
    {
        // CraftableOnly closes over Analysis.CurrentLevel — re-run the filter when it shifts.
        RecipesView.Refresh();
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

    // ── Sort wiring ──────────────────────────────────────────────────────

    private void HydrateSortKeysFromSettings()
    {
        var byId = AvailableSortKeys.ToDictionary(k => k.Id);

        // 1. New persisted list — restore ordered entries, skipping any whose Id no
        //    longer maps (renamed / removed).
        foreach (var entry in _settings.ActiveSortKeys)
        {
            if (byId.TryGetValue(entry.Id, out var key))
                ActiveSortKeys.Add(new ActiveSortKey<RecipeAnalysis>(key, entry.Direction));
        }

        // 2. Legacy single-key migration: SortKey + SortDescending from the pre-popup
        //    schema, only honoured when the new list didn't contain anything resolvable.
        if (ActiveSortKeys.Count == 0
            && !string.IsNullOrEmpty(_settings.SortKey)
            && byId.TryGetValue(_settings.SortKey, out var legacy))
        {
            var dir = (_settings.SortDescending ?? legacy.DefaultDescending)
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            ActiveSortKeys.Add(new ActiveSortKey<RecipeAnalysis>(legacy, dir));
        }

        // 3. Default seed: most-effective XP first.
        if (ActiveSortKeys.Count == 0)
        {
            ActiveSortKeys.Add(new ActiveSortKey<RecipeAnalysis>(
                byId["EffectiveXp"], ListSortDirection.Descending));
        }

        // Drop the legacy fields so they don't shadow the new list on next save.
        _settings.SortKey = null;
        _settings.SortDescending = null;
    }

    private void OnActiveSortKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ActiveSortKey<RecipeAnalysis> k in e.OldItems)
                k.PropertyChanged -= OnActiveSortKeyPropertyChanged;
        if (e.NewItems is not null)
            foreach (ActiveSortKey<RecipeAnalysis> k in e.NewItems)
                k.PropertyChanged += OnActiveSortKeyPropertyChanged;
        if (e.Action == NotifyCollectionChangedAction.Reset)
            foreach (var k in ActiveSortKeys)
                k.PropertyChanged += OnActiveSortKeyPropertyChanged;

        PersistAndApplySort();
    }

    private void OnActiveSortKeyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveSortKey<RecipeAnalysis>.Direction))
            PersistAndApplySort();
    }

    private void PersistAndApplySort()
    {
        _settings.ActiveSortKeys = ActiveSortKeys
            .Select(k => new PersistedSortEntry(k.Key.Id, k.Direction))
            .ToList();
        ApplySortDescriptions();
    }

    private void ApplySortDescriptions()
    {
        using (RecipesView.DeferRefresh())
        {
            RecipesView.SortDescriptions.Clear();
            foreach (var k in ActiveSortKeys)
                RecipesView.SortDescriptions.Add(new SortDescription(k.Key.SortMemberPath, k.Direction));
        }
    }

    // ── Filter wiring ────────────────────────────────────────────────────

    private void HydrateFiltersFromSettings()
    {
        // First launch (no persisted filters): keep the constructor-declared defaults.
        // Otherwise, mirror the persisted set onto IsActive — Ids no longer in code
        // are silently ignored.
        if (_settings.ActiveFilterIds is { Count: 0 } && !_settings.HasPersistedFilters) return;

        var persisted = new HashSet<string>(_settings.ActiveFilterIds);
        foreach (var f in AvailableFilters)
            f.IsActive = persisted.Contains(f.Id);
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FilterPredicate<RecipeAnalysis>.IsActive)) return;

        _settings.ActiveFilterIds = AvailableFilters
            .Where(f => f.IsActive)
            .Select(f => f.Id)
            .ToList();
        _settings.HasPersistedFilters = true;
        RecipesView.Refresh();
    }

    private bool MatchesActiveFilters(RecipeAnalysis row)
    {
        if (Analysis is null) return false;
        foreach (var f in AvailableFilters)
        {
            if (!f.ShouldApply) continue;
            if (!f.Predicate(row)) return false;
        }
        return true;
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

        // Always include 0-XP recipes in the analysis result; the "Hide 0 XP" filter
        // takes care of hiding them at the view level. Keeps engine output stable
        // regardless of filter state.
        Analysis = _engine.Analyze(SelectedSkill, active, includeZeroXp: true, GoalLevel);

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

    private void OnCurrentRecipeChanged(object? sender, EventArgs e)
    {
        SelectedRecipe = RecipesView.CurrentItem as RecipeAnalysis;
    }

    private void OnActiveCharacterChanged(object? sender, EventArgs e)
    {
        OnUiThread(() =>
        {
            ReloadSkills();
            Reanalyze();
        });
    }

    private void OnReferenceUpdated(object? sender, string key)
    {
        if (key is not ("recipes" or "skills" or "xptables")) return;
        OnUiThread(() =>
        {
            ReloadSkills();
            Reanalyze();
        });
    }

    /// <summary>
    /// Reanalyze mutates <see cref="_recipes"/>, which is observed by a
    /// <see cref="ListCollectionView"/> that rejects changes off the
    /// dispatcher thread. ReferenceData and ActiveCharacter events both fire
    /// from background threads, so marshal here. Otherwise the view's
    /// internal state desyncs and subsequent refreshes (e.g. a sort change)
    /// surface zero items.
    /// </summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.InvokeAsync(action, DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        _activeChar.ActiveCharacterChanged -= OnActiveCharacterChanged;
        _activeChar.CharacterExportsChanged -= OnActiveCharacterChanged;
        _referenceData.FileUpdated -= OnReferenceUpdated;
        RecipesView.CurrentChanged -= OnCurrentRecipeChanged;
        ActiveSortKeys.CollectionChanged -= OnActiveSortKeysChanged;
        foreach (var k in ActiveSortKeys) k.PropertyChanged -= OnActiveSortKeyPropertyChanged;
        foreach (var f in AvailableFilters) f.PropertyChanged -= OnFilterPropertyChanged;
    }
}
