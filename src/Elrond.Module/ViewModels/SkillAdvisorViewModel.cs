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

        BuildSkillTree();
    }

    // ── Observable properties ────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<SkillNode> _skillTreeRoots = [];

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
    /// by <see cref="BuildSkillTree"/> after each rebuild.
    /// </summary>
    private string? _pendingDeepLinkSkill;

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
        UpdateSelectedSkillSummary(value);
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
        var leafKeys = CollectLeafKeys(SkillTreeRoots);
        if (active is not null && leafKeys.Contains(skillKey))
        {
            _pendingDeepLinkSkill = null;
            SelectedSkill = skillKey;
            return;
        }

        // Stash and apply after the next tree rebuild. Surface a status hint so
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

    private void BuildSkillTree()
    {
        var active = _activeChar.ActiveCharacter;
        if (active is null)
        {
            SkillTreeRoots = [];
            SelectedSkill = null;
            return;
        }

        // Leaf set: cookbook sections (SortSkill ?? RewardSkill) the character has the
        // section's own skill for. Sections that aren't real character skills
        // (e.g. Race_Fae) drop out — the engine can't advise on them.
        var leafKeys = _engine.GetCookbookSections()
            .Where(k => active.Skills.ContainsKey(k))
            .ToList();

        // Build leaf nodes carrying the character's current level/XP.
        var leaves = new List<SkillNode>(leafKeys.Count);
        foreach (var key in leafKeys)
        {
            var displayName = _referenceData.Skills.TryGetValue(key, out var entry) ? entry.DisplayName : key;
            var charSkill = active.Skills[key];
            leaves.Add(new SkillNode(
                Key: key,
                DisplayName: displayName,
                CurrentLevel: charSkill.Level,
                CurrentXp: charSkill.XpTowardNextLevel,
                XpNeededForNextLevel: charSkill.XpNeededForNextLevel,
                IsHeaderOnly: false,
                Children: []));
        }

        // Group leaves by their immediate parent (skills.json Parents[0]).
        // A leaf with no parent → potential root. A leaf with a parent → goes into
        // its parent's bucket; the parent will be materialized as a tree node below.
        var leafByKey = leaves.ToDictionary(l => l.Key, StringComparer.Ordinal);
        var parentBuckets = new Dictionary<string, List<SkillNode>>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            var parentKey = _referenceData.Skills.TryGetValue(leaf.Key, out var entry)
                ? entry.Parents.FirstOrDefault()
                : null;
            if (string.IsNullOrEmpty(parentKey)) continue;
            if (!parentBuckets.TryGetValue(parentKey, out var bucket))
            {
                bucket = [];
                parentBuckets[parentKey] = bucket;
            }
            bucket.Add(leaf);
        }

        // Build roots: every leaf with no parent. A leaf with a parent is nested
        // either under a sibling-leaf (if its parent is in our set) or under a
        // pure header node materialized below. If a root leaf has children of
        // its own, attach them — produces ONE selectable node with nested
        // children rather than duplicating the leaf alongside a separate header.
        var rootsList = new List<SkillNode>();
        foreach (var leaf in leaves)
        {
            var parentKey = _referenceData.Skills.TryGetValue(leaf.Key, out var entry)
                ? entry.Parents.FirstOrDefault()
                : null;
            if (!string.IsNullOrEmpty(parentKey)) continue;
            if (parentBuckets.TryGetValue(leaf.Key, out var ownChildren))
            {
                var sortedChildren = ownChildren
                    .OrderBy(c => c.DisplayName, StringComparer.Ordinal)
                    .ToList();
                rootsList.Add(leaf with { Children = sortedChildren });
            }
            else
            {
                rootsList.Add(leaf);
            }
        }

        // Pure header parents — referenced by children but not themselves a leaf
        // (e.g. "Augmentation" when none of its child Augment* skills file recipes
        // directly under Augmentation itself). Materialize as header-only nodes.
        foreach (var (parentKey, children) in parentBuckets)
        {
            if (leafByKey.ContainsKey(parentKey)) continue;
            var parentDisplay = _referenceData.Skills.TryGetValue(parentKey, out var pe) ? pe.DisplayName : parentKey;
            var sortedChildren = children
                .OrderBy(c => c.DisplayName, StringComparer.Ordinal)
                .ToList();
            rootsList.Add(new SkillNode(
                Key: parentKey,
                DisplayName: parentDisplay,
                CurrentLevel: null,
                CurrentXp: null,
                XpNeededForNextLevel: null,
                IsHeaderOnly: true,
                Children: sortedChildren));
        }

        rootsList = rootsList
            .OrderBy(n => n.DisplayName, StringComparer.Ordinal)
            .ToList();

        SkillTreeRoots = new ObservableCollection<SkillNode>(rootsList);

        // Select: pending deep-link wins, then last-persisted skill, then first leaf.
        var allLeafKeys = new HashSet<string>(leafKeys, StringComparer.Ordinal);
        if (_pendingDeepLinkSkill is { } pending && allLeafKeys.Contains(pending))
        {
            _pendingDeepLinkSkill = null;
            SelectedSkill = pending;
        }
        else if (allLeafKeys.Contains(_settings.LastSkill ?? string.Empty))
        {
            SelectedSkill = _settings.LastSkill;
        }
        else
        {
            SelectedSkill = leafKeys.FirstOrDefault();
        }
    }

    private static HashSet<string> CollectLeafKeys(IEnumerable<SkillNode> nodes)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node.IsHeaderOnly)
            {
                foreach (var child in CollectLeafKeys(node.Children)) set.Add(child);
            }
            else
            {
                set.Add(node.Key);
            }
        }
        return set;
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
            BuildSkillTree();
            Reanalyze();
        });
    }

    private void OnReferenceUpdated(object? sender, string key)
    {
        if (key is not ("recipes" or "skills" or "xptables")) return;
        OnUiThread(() =>
        {
            BuildSkillTree();
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
