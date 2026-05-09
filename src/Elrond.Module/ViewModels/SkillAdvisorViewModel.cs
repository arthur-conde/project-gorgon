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
        _onlyAlreadyLearnedRecipes = settings.SimOnlyAlreadyLearnedRecipes;
        _useFirstTimeBonuses = settings.SimUseFirstTimeBonuses;

        // Filters declared here so each closure captures `this` and reads live state
        // (e.g. CraftableOnly compares against the current Analysis at predicate-call time).
        // Labels read from the user's perspective: toggling on does what the label says.
        AvailableFilters =
        [
            // Inverted: predicate r => r.IsKnown applies when IsActive=false; toggling ON reveals unknowns.
            new("ShowUnknown",        "Show unknown",          r => r.IsKnown,                inverted: true,  isActive: false),
            new("FirstTimeBonusOnly", "First Time Bonus Only", r => r.FirstTimeBonusAvailable, inverted: false, isActive: false),
            // CraftableOnly compares against the recipe's *gating* skill, not the section's level.
            // In umbrella sections (Phrenology files Phrenology_Goblins recipes) those differ —
            // see SkillAdvisorEngine.Analyze for how GatingSkillCurrentLevel is populated.
            new("CraftableOnly",      "Craftable only",        r => r.LevelRequired <= r.GatingSkillCurrentLevel, inverted: false, isActive: true),
            new("HideZeroXp",         "Hide 0 XP",             r => r.EffectiveXp > 0,         inverted: false, isActive: true),
            // Cookbook view shows recipes filed under a section regardless of which skill they level
            // (a fish dish in Cooking levels Fishing). Toggling this filter on hides the mixed-reward
            // recipes — useful when the user wants the classic "what levels skill X" view.
            new("RewardingSectionOnly", "Only recipes that level this skill",
                r => Analysis is { } a && r.RewardSkill.Equals(a.SkillName, StringComparison.Ordinal),
                inverted: false, isActive: false),
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

        BuildSkillNodes();
    }

    // ── Observable properties ────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<SkillNode> _skillNodes = [];

    [ObservableProperty]
    private string? _selectedSkill;

    [ObservableProperty]
    private string? _selectedSkillDisplayName;

    [ObservableProperty]
    private int? _selectedSkillLevel;

    /// <summary>
    /// Skill key requested via <see cref="SelectSkillFromDeepLink"/> that couldn't
    /// be applied at request time (reference data not yet loaded, no active
    /// character, or the skill not in the active character's known set). Consumed
    /// by <see cref="BuildSkillNodes"/> after each rebuild.
    /// </summary>
    private string? _pendingDeepLinkSkill;

    [ObservableProperty]
    private SkillAnalysis? _analysis;

    /// <summary>
    /// Section-header strings backed by <see cref="Analysis"/>. The partial
    /// OnAnalysisChanged raises change notifications so the bound TextBlocks
    /// refresh. <see cref="SectionLevelText"/> and <see cref="SectionBonusLevelsText"/>
    /// always render the export's values (even for umbrellas — Phrenology has a
    /// real Level even though it has no XpTable). The XP fraction / remaining-line
    /// text degrades to <c>—</c> for umbrellas because the export uses 0/1 sentinels
    /// there and rendering those would be misleading.
    /// </summary>
    public string SectionLevelText => Analysis switch
    {
        null => "—",
        var a => a.CurrentLevel.ToString(),
    };
    public string SectionBonusLevelsText => Analysis switch
    {
        { CurrentBonusLevels: > 0 } a => $" ({a.CurrentBonusLevels} from bonuses)",
        _ => string.Empty,
    };
    public string SectionCurrentXpText => Analysis switch
    {
        null => "—",
        { IsUmbrellaSection: true } => "—",
        var a => a.CurrentXp.ToString("N0"),
    };
    public string SectionXpNeededText => Analysis switch
    {
        null => "—",
        { IsUmbrellaSection: true } => "—",
        var a => a.XpNeededForNextLevel.ToString("N0"),
    };
    public string SectionXpRemainingText => Analysis switch
    {
        null => "—",
        { IsUmbrellaSection: true } => "—",
        var a => a.XpRemaining.ToString("N0"),
    };

    [ObservableProperty]
    private RecipeAnalysis? _selectedRecipe;

    [ObservableProperty]
    private string _statusMessage = "Select a character export to begin.";

    [ObservableProperty]
    private int? _goalLevel;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseResultAsNextStartCommand))]
    private SimulationResult? _simulationResult;

    [ObservableProperty]
    private string _viewMode = "Rows";

    // ── Simulator decoupled state ────────────────────────────────────────
    // The Recipes tab continues to read live from _activeChar; only the
    // Simulation tab is decoupled so the user can forecast from a future
    // hypothetical state, chain runs, and toggle constraints. Inputs are
    // transient (no persistence beyond constraint toggles).

    /// <summary>
    /// Frozen snapshot the simulator runs against. <c>null</c> falls back to
    /// the live active character — preserves the zero-click "from now" UX.
    /// Populated by <see cref="CopyFromCurrentCharacterCommand"/> or
    /// <see cref="UseResultAsNextStartCommand"/>.
    /// </summary>
    [ObservableProperty]
    private CharacterSnapshot? _simulationStartState;

    /// <summary>
    /// User-supplied override for the selected skill's starting level. <c>null</c>
    /// means "use whatever's in the snapshot" (active or chained). Empty TextBox
    /// in the UI maps to <c>null</c>.
    /// </summary>
    [ObservableProperty]
    private int? _simulationStartLevel;

    /// <summary>
    /// User-supplied override for the selected skill's starting XP-toward-next-level.
    /// </summary>
    [ObservableProperty]
    private long? _simulationStartXpTowardNext;

    /// <summary>
    /// Caption describing where the working snapshot came from. Independent of the
    /// level/XP override fields, which only modify the *selected skill's* row.
    /// </summary>
    [ObservableProperty]
    private string _simulationStartSource = "Active character";

    [ObservableProperty]
    private bool _onlyAlreadyLearnedRecipes;

    [ObservableProperty]
    private bool _useFirstTimeBonuses = true;

    // ── Property change handlers ─────────────────────────────────────────

    partial void OnSelectedSkillChanged(string? value)
    {
        if (value is not null)
            _settings.LastSkill = value;
        UpdateSelectedSkillSummary(value);
        ResetSimulationStartState();
        Reanalyze();
    }

    partial void OnOnlyAlreadyLearnedRecipesChanged(bool value)
        => _settings.SimOnlyAlreadyLearnedRecipes = value;

    partial void OnUseFirstTimeBonusesChanged(bool value)
        => _settings.SimUseFirstTimeBonuses = value;

    partial void OnGoalLevelChanged(int? value)
    {
        _settings.LastGoalLevel = value;
        SimulationResult = null;
        Reanalyze();
    }

    partial void OnAnalysisChanged(SkillAnalysis? value)
    {
        // Filter predicates (RewardingSectionOnly) close over Analysis — re-run them when it shifts.
        RecipesView.Refresh();
        // Section-header text properties are computed from Analysis; nudge the bound
        // TextBlocks to re-evaluate now that the source has changed.
        OnPropertyChanged(nameof(SectionLevelText));
        OnPropertyChanged(nameof(SectionBonusLevelsText));
        OnPropertyChanged(nameof(SectionCurrentXpText));
        OnPropertyChanged(nameof(SectionXpNeededText));
        OnPropertyChanged(nameof(SectionXpRemainingText));
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
        if (string.IsNullOrEmpty(SelectedSkill) || GoalLevel is not { } goal)
        {
            SimulationResult = null;
            return;
        }

        var startState = BuildSimulationStartState(SelectedSkill);
        if (startState is null)
        {
            SimulationResult = null;
            return;
        }

        var constraints = new SimulationConstraints(
            OnlyAlreadyLearnedRecipes: OnlyAlreadyLearnedRecipes,
            UseFirstTimeBonuses: UseFirstTimeBonuses);

        SimulationResult = _simulator.Simulate(SelectedSkill, startState, goal, constraints);
    }

    /// <summary>
    /// Snapshots the live active character into <see cref="SimulationStartState"/>
    /// and seeds the Level / XP inputs from the selected skill. Lets the user edit
    /// from a known starting point rather than typing values from scratch.
    /// </summary>
    [RelayCommand]
    private void CopyFromCurrentCharacter()
    {
        var active = _activeChar.ActiveCharacter;
        if (active is null) return;

        SimulationStartState = active;
        SimulationStartSource = "Active character (snapshot)";

        if (!string.IsNullOrEmpty(SelectedSkill) && active.Skills.TryGetValue(SelectedSkill, out var s))
        {
            SimulationStartLevel = s.Level;
            SimulationStartXpTowardNext = s.XpTowardNextLevel;
        }
    }

    /// <summary>
    /// Feeds the most recent <see cref="SimulationResult.FinalState"/> back in as
    /// the next simulation's starting point. Enables chained forecasts (Lv 30 → 50,
    /// then 50 → 70 *with* the recipes consumed by the first run already crossed
    /// off the first-time-bonus list).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseResultAsNextStart))]
    private void UseResultAsNextStart()
    {
        if (SimulationResult is not { } result) return;

        SimulationStartState = result.FinalState;
        SimulationStartSource = "Previous simulation result";

        if (!string.IsNullOrEmpty(SelectedSkill)
            && result.FinalState.Skills.TryGetValue(SelectedSkill, out var s))
        {
            SimulationStartLevel = s.Level;
            SimulationStartXpTowardNext = s.XpTowardNextLevel;
        }

        // Clear so the user clicks Simulate again with the carried-over inputs.
        SimulationResult = null;
    }

    private bool CanUseResultAsNextStart() => SimulationResult is not null;

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

    /// <summary>
    /// Public deep-link entry point for <c>mithril://elrond/{skillKey}</c>. Selects
    /// the named skill if it's already in the active character's known set. If the
    /// view-model isn't ready (no character, no reference data, or character doesn't
    /// know the skill yet) the request is stashed and applied after the next tree
    /// rebuild — covers character-switch and CDN-refresh races.
    /// </summary>
    public void SelectSkillFromDeepLink(string skillKey)
    {
        if (string.IsNullOrEmpty(skillKey)) return;

        var active = _activeChar.ActiveCharacter;
        var leafKeys = SkillNodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        if (active is not null && leafKeys.Contains(skillKey))
        {
            _pendingDeepLinkSkill = null;
            SelectedSkill = skillKey;
            return;
        }

        // Stash and apply after the next list rebuild. Surface a status hint so
        // the user knows we received the request even when we can't act on it yet.
        _pendingDeepLinkSkill = skillKey;
        if (active is null)
        {
            StatusMessage = $"Deep link for '{skillKey}' will apply once a character is active.";
        }
        else
        {
            var displayName = _referenceData.Skills.TryGetValue(skillKey, out var entry)
                ? entry.DisplayName
                : skillKey;
            StatusMessage = $"Elrond cannot advise on '{displayName}' for {active.Name} — the character has no recipes for it.";
        }
    }

    private void BuildSkillNodes()
    {
        var active = _activeChar.ActiveCharacter;
        if (active is null)
        {
            SkillNodes = [];
            SelectedSkill = null;
            return;
        }

        // Cookbook sections (SortSkill ?? RewardSkill) the character has the section's
        // own skill for. Sections that aren't real character skills (e.g. Race_Fae)
        // drop out — the engine can't advise on them. Sorted alphabetically by display
        // name; flat list, no hierarchy (the picker organises by recipe filing, which
        // is flat in the in-game cookbook).
        var nodes = _engine.GetCookbookSections()
            .Where(k => active.Skills.ContainsKey(k))
            .Select(k =>
            {
                var displayName = _referenceData.Skills.TryGetValue(k, out var entry) ? entry.DisplayName : k;
                var charSkill = active.Skills[k];
                return new SkillNode(
                    Key: k,
                    DisplayName: displayName,
                    CurrentLevel: charSkill.Level,
                    CurrentXp: charSkill.XpTowardNextLevel,
                    XpNeededForNextLevel: charSkill.XpNeededForNextLevel);
            })
            .OrderBy(n => n.DisplayName, StringComparer.Ordinal)
            .ToList();

        SkillNodes = new ObservableCollection<SkillNode>(nodes);

        // Select: pending deep-link wins, then last-persisted skill, then first node.
        var allKeys = new HashSet<string>(nodes.Select(n => n.Key), StringComparer.Ordinal);
        if (_pendingDeepLinkSkill is { } pending && allKeys.Contains(pending))
        {
            _pendingDeepLinkSkill = null;
            SelectedSkill = pending;
        }
        else if (allKeys.Contains(_settings.LastSkill ?? string.Empty))
        {
            SelectedSkill = _settings.LastSkill;
        }
        else
        {
            SelectedSkill = nodes.Select(n => n.Key).FirstOrDefault();
        }
    }

    private void UpdateSelectedSkillSummary(string? skillKey)
    {
        if (skillKey is null)
        {
            SelectedSkillDisplayName = null;
            SelectedSkillLevel = null;
            return;
        }

        SelectedSkillDisplayName = _referenceData.Skills.TryGetValue(skillKey, out var entry)
            ? entry.DisplayName
            : skillKey;

        var active = _activeChar.ActiveCharacter;
        SelectedSkillLevel = active is not null && active.Skills.TryGetValue(skillKey, out var charSkill)
            ? charSkill.Level
            : null;
    }

    /// <summary>
    /// Resolves the snapshot the simulator should run against, applying the
    /// user-supplied level/XP overrides for the selected skill if set. Falls
    /// back to the live active character when no snapshot has been captured.
    /// Returns null when no character is available at all.
    /// </summary>
    private CharacterSnapshot? BuildSimulationStartState(string skillKey)
    {
        var baseSnapshot = SimulationStartState ?? _activeChar.ActiveCharacter;
        if (baseSnapshot is null) return null;

        // No overrides → return the snapshot as-is.
        if (SimulationStartLevel is null && SimulationStartXpTowardNext is null)
            return baseSnapshot;

        if (!baseSnapshot.Skills.TryGetValue(skillKey, out var existingSkill))
            return baseSnapshot;

        var newLevel = SimulationStartLevel ?? existingSkill.Level;
        var newXp = SimulationStartXpTowardNext ?? existingSkill.XpTowardNextLevel;

        // Re-derive XpNeededForNextLevel from the XP table for the new level
        // (otherwise editing level-up would carry the *old* level's curve point).
        var xpAmounts = _engine.ResolveXpTable(skillKey);
        long newXpNeeded = xpAmounts is not null && newLevel - 1 >= 0 && newLevel - 1 < xpAmounts.Count
            ? xpAmounts[newLevel - 1]
            : existingSkill.XpNeededForNextLevel;

        var newSkills = new Dictionary<string, CharacterSkill>(baseSnapshot.Skills, StringComparer.Ordinal)
        {
            [skillKey] = new CharacterSkill(newLevel, existingSkill.BonusLevels, newXp, newXpNeeded),
        };

        return new CharacterSnapshot(
            baseSnapshot.Name,
            baseSnapshot.Server,
            baseSnapshot.ExportedAt,
            newSkills,
            baseSnapshot.RecipeCompletions,
            baseSnapshot.NpcFavor);
    }

    /// <summary>
    /// Drops any captured snapshot and clears the level/XP overrides. Called
    /// when the selected skill or active character changes — both invalidate
    /// any in-flight "what-if" the user was building.
    /// </summary>
    private void ResetSimulationStartState()
    {
        SimulationStartState = null;
        SimulationStartLevel = null;
        SimulationStartXpTowardNext = null;
        SimulationStartSource = "Active character";
        SimulationResult = null;
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
            // Different character → simulator's previous what-if is no longer
            // meaningful (the snapshot was for the old character). Reset before
            // rebuilding the skill list so OnSelectedSkillChanged doesn't have
            // stale state to clear.
            ResetSimulationStartState();
            BuildSkillNodes();
            Reanalyze();
        });
    }

    private void OnReferenceUpdated(object? sender, string key)
    {
        if (key is not ("recipes" or "skills" or "xptables")) return;
        OnUiThread(() =>
        {
            BuildSkillNodes();
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
