using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Elrond.Domain;
using Elrond.Services;
using Mithril.GameReports;
using Mithril.Shared.Character;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Filtering;
using Mithril.Shared.Wpf.Query;
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
        new("RecipeName",         "Recipe"),
        new("LevelRequired",      "Lvl Req"),
        new("EffectiveXp",        "Eff. XP",     DefaultDescending: true),
        new("Complexity",         "Complexity"),
        new("Efficiency",         "Efficiency",  DefaultDescending: true),
        new("CompletionsToLevel", "To Level",    DefaultDescending: true),
    ];

    public IReadOnlyList<FilterPredicate<RecipeAnalysis>> AvailableFilters { get; }

    /// <summary>
    /// Reflected schema for the <see cref="MithrilQueryBox"/> so highlighter and
    /// completion both know which columns are real on a <see cref="RecipeAnalysis"/>.
    /// </summary>
    public IReadOnlyList<ColumnSchema> Schema { get; } = ColumnBindingHelper.ToSchema(
        ColumnBindingHelper.BuildFromProperties(typeof(RecipeAnalysis)));

    public IReadOnlyList<ChipState<RecipeAnalysis>> Chips =>
        _controller?.Chips ?? Array.Empty<ChipState<RecipeAnalysis>>();

    private SortFilterController<RecipeAnalysis>? _controller;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    private readonly SkillAdvisorEngine _engine;
    private readonly IActiveCharacterService _activeChar;
    private readonly LiveProgressionAdapter _progression;
    private readonly IReferenceDataService _referenceData;
    private readonly ElrondSettings _settings;

    private CharacterSnapshot? ActiveCharacterSnapshot => _progression.GetMergedSnapshot();
    // Resolved lazily, ONLY on the explicit Send click. Resolving ICraftListImportTarget
    // eagerly at ctor time closes a DI cycle: it pulls Celebrimbor's CraftListImportTarget
    // → IModuleActivator → ShellModuleActivator(ShellViewModel) → ShellViewModel..ctor's
    // eager ActivateModule() → back to this VM, before any singleton is cached. MS.DI's
    // StackGuard turns that into a silent UI-thread deadlock (boot.log stops at
    // "creating App"), invisible to unit tests. Deferring to user-action time breaks it.
    private readonly Func<ICraftListImportTarget?>? _craftListImportAccessor;

    private readonly ObservableCollection<RecipeAnalysis> _recipes = [];

    public ICollectionView RecipesView { get; }

    /// <summary>
    /// The "Generate leveling plan" surface (#228 PR-B/B2), hosted as a peer
    /// tab. Seeded — but not clobbered — from the advisor's current skill/goal.
    /// </summary>
    public GenerateLevelingPlanViewModel GeneratePlan { get; }

    public SkillAdvisorViewModel(
        SkillAdvisorEngine engine,
        IActiveCharacterService characterData,
        LiveProgressionAdapter progression,
        IReferenceDataService referenceData,
        ElrondSettings settings,
        GenerateLevelingPlanViewModel generatePlan,
        Func<ICraftListImportTarget?>? craftListImportAccessor = null)
    {
        _engine = engine;
        _activeChar = characterData;
        _progression = progression;
        _referenceData = referenceData;
        _settings = settings;
        GeneratePlan = generatePlan;
        _craftListImportAccessor = craftListImportAccessor;

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
        view.CurrentChanged += OnCurrentRecipeChanged;
        RecipesView = view;

        // Wire the controller: it now owns the view's Filter + SortDescriptions and
        // republishes Chips whenever the parsed ORDER BY changes. Chip clicks come
        // back as a callback that rewrites the QueryText, which round-trips through
        // OnQueryTextChanged → controller.OnParsedOrderChanged → new Chips.
        _controller = new SortFilterController<RecipeAnalysis>(
            view,
            AvailableSortKeys,
            AvailableFilters,
            newOrder =>
            {
                QueryText = OrderClauseRewriter.Rewrite(QueryText, newOrder);
            });
        _controller.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SortFilterController<RecipeAnalysis>.Chips))
                OnPropertyChanged(nameof(Chips));
        };

        HydrateFiltersFromSettings();
        foreach (var f in AvailableFilters) f.PropertyChanged += OnFilterPropertyChanged;

        _progression.Changed += OnProgressionChanged;
        _referenceData.FileUpdated += OnReferenceUpdated;

        BuildSkillNodes();

        // Initial QueryText: prefer the persisted text; otherwise migrate from the
        // legacy ActiveSortKeys / SortKey schema; otherwise seed with the default
        // ORDER BY EffectiveXp DESC. Set last so the OnQueryTextChanged handler
        // seeds chip state through the normal pipeline.
        QueryText = HydrateInitialQueryText();
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
    [NotifyCanExecuteChangedFor(nameof(SendToCraftListCommand))]
    private RecipeAnalysis? _selectedRecipe;

    [ObservableProperty]
    private string _statusMessage = "Select a character export to begin.";

    [ObservableProperty]
    private int? _goalLevel;

    [ObservableProperty]
    private string _viewMode = "Rows";

    // ── Property change handlers ─────────────────────────────────────────

    partial void OnSelectedSkillChanged(string? value)
    {
        if (value is not null)
            _settings.LastSkill = value;
        UpdateSelectedSkillSummary(value);
        GeneratePlan?.SeedFromAdvisor(value, GoalLevel);
        Reanalyze();
    }

    partial void OnGoalLevelChanged(int? value)
    {
        _settings.LastGoalLevel = value;
        GeneratePlan?.SeedFromAdvisor(SelectedSkill, value);
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

    /// <summary>
    /// Parse <see cref="QueryText"/> and forward the parsed ORDER BY to the
    /// controller. Parse errors are surfaced via <see cref="QueryError"/> and
    /// leave the previous sort/filter intact. The query text itself is always
    /// persisted (even on parse error) — the user is mid-typing and would
    /// expect their input to round-trip across launches.
    /// </summary>
    partial void OnQueryTextChanged(string value)
    {
        _settings.LastQueryText = value;
        try
        {
            var parsed = QueryParser.Parse(value) ?? ParsedQuery.Empty;
            QueryError = null;
            _controller?.OnParsedOrderChanged(parsed.Order);
        }
        catch (QueryException ex)
        {
            QueryError = ex.Message;
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void RefreshCharacterData()
    {
        _activeChar.Refresh();
    }

    /// <summary>
    /// True when the craft-list import path is wired (Celebrimbor present). Checks only
    /// whether the accessor exists — it does NOT resolve the target, so command-can-execute
    /// re-queries during boot can't re-enter the DI graph (see the accessor field comment).
    /// </summary>
    public bool IsCraftListImportAvailable => _craftListImportAccessor is not null;

    /// <summary>
    /// Pushes the selected recipe into the craft-list module (Celebrimbor) in-process —
    /// activates its tab and runs the shared Append/Replace/Cancel dialog. v1 sends quantity
    /// 1 per recipe; the user dials in counts in Celebrimbor. The importer contract is
    /// list-based, so multi-select is a non-breaking UI follow-up.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendToCraftList))]
    private void SendToCraftList()
    {
        if (SelectedRecipe is not { } recipe || string.IsNullOrWhiteSpace(recipe.InternalName)) return;

        // Resolve the target HERE — on the user's click, long after boot — so the
        // ICraftListImportTarget → ShellViewModel resolution path can never run while
        // the container is still building the shell graph.
        var target = _craftListImportAccessor?.Invoke();
        if (target is null) return;

        target.ImportRecipes(
            [new CraftListImportEntry(recipe.InternalName, 1)],
            $"Elrond · {recipe.RecipeName}");
    }

    private bool CanSendToCraftList()
        => _craftListImportAccessor is not null
           && SelectedRecipe is { } r
           && !string.IsNullOrWhiteSpace(r.InternalName);

    /// <summary>
    /// Toggle the chip with the given Id. Bound to chip clicks in the sort popup.
    /// Routes through the controller, which rewrites the query box's ORDER BY clause.
    /// </summary>
    [RelayCommand]
    private void ToggleChip(string id) => _controller?.ToggleChip(id);

    // ── ISortableViewModel ───────────────────────────────────────────────

    void ISortableViewModel.ToggleChip(string id) => _controller?.ToggleChip(id);

    // ── Settings hydration ───────────────────────────────────────────────

    /// <summary>
    /// Compute the initial <see cref="QueryText"/>: prefer the persisted text;
    /// otherwise migrate from the legacy schema (ActiveSortKeys / SortKey+SortDescending);
    /// otherwise default to "ORDER BY EffectiveXp DESC". Clears legacy fields after
    /// reading so the next save doesn't shadow the new field.
    /// </summary>
    private string HydrateInitialQueryText()
    {
        if (!string.IsNullOrEmpty(_settings.LastQueryText))
            return _settings.LastQueryText;

        // Legacy multi-key schema → synthesize an ORDER BY clause.
        if (_settings.ActiveSortKeys.Count > 0)
        {
            var available = new HashSet<string>(
                AvailableSortKeys.Select(k => k.Id), StringComparer.OrdinalIgnoreCase);
            var specs = _settings.ActiveSortKeys
                .Where(e => available.Contains(e.Id))
                .Select(e => new OrderSpec(
                    e.Id,
                    e.Direction == ListSortDirection.Ascending
                        ? OrderDirection.Ascending
                        : OrderDirection.Descending))
                .ToArray();
            _settings.ActiveSortKeys = [];
            if (specs.Length > 0)
                return OrderClauseRewriter.FormatOrderClause(specs);
        }

        // Legacy single-key schema.
        if (!string.IsNullOrEmpty(_settings.SortKey))
        {
            var legacy = AvailableSortKeys.FirstOrDefault(k =>
                string.Equals(k.Id, _settings.SortKey, StringComparison.OrdinalIgnoreCase));
            if (legacy is not null)
            {
                bool desc = _settings.SortDescending ?? legacy.DefaultDescending;
                var direction = desc ? OrderDirection.Descending : OrderDirection.Ascending;
                _settings.SortKey = null;
                _settings.SortDescending = null;
                return OrderClauseRewriter.FormatOrderClause(
                    [new OrderSpec(legacy.Id, direction)]);
            }
            _settings.SortKey = null;
            _settings.SortDescending = null;
        }

        // Default seed: most-effective XP first.
        return "ORDER BY EffectiveXp DESC";
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

        var active = ActiveCharacterSnapshot;
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
        var active = ActiveCharacterSnapshot;
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

        var active = ActiveCharacterSnapshot;
        SelectedSkillLevel = active is not null && active.Skills.TryGetValue(skillKey, out var charSkill)
            ? charSkill.Level
            : null;
    }

    private void Reanalyze()
    {
        var active = ActiveCharacterSnapshot;
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
            StatusMessage = FormatStatusMessage(active);
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

    private void OnProgressionChanged()
    {
        OnUiThread(() =>
        {
            BuildSkillNodes();
            Reanalyze();
        });
    }

    private string FormatStatusMessage(CharacterSnapshot active)
    {
        var baseLine = $"{active.Name} on {active.Server}";
        return _progression.LastDataSource switch
        {
            ProgressionDataSource.Merged when _progression.ExportTimestamp is { } exportAt =>
                $"{baseLine} — live + export (export {exportAt:g})",
            ProgressionDataSource.LiveOnly =>
                $"{baseLine} — live tracking (no export yet)",
            ProgressionDataSource.ExportOnly =>
                $"{baseLine} — exported {active.ExportedAt:g}",
            _ => $"{baseLine} — exported {active.ExportedAt:g}",
        };
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
        _progression.Changed -= OnProgressionChanged;
        _referenceData.FileUpdated -= OnReferenceUpdated;
        RecipesView.CurrentChanged -= OnCurrentRecipeChanged;
        foreach (var f in AvailableFilters) f.PropertyChanged -= OnFilterPropertyChanged;
        _controller?.Dispose();
    }
}
