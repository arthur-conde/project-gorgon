using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Query;

namespace Celebrimbor.ViewModels;

/// <summary>
/// Viewer for the contents of a single <c>tsysprofiles</c> pool. The expensive
/// <see cref="EffectDescsRenderer"/> work runs on a background thread so the window
/// paints immediately with a "Loading…" placeholder, then swaps in the option list
/// when expansion completes.
///
/// Filter parsing uses the shared <see cref="QueryCompiler"/> directly. Per-tier
/// rows go through the predicate first; surviving rows are then grouped by
/// (power, suffix, skill) into one card per power.
/// </summary>
public sealed partial class AugmentPoolViewModel : ObservableObject
{
    private static readonly Dictionary<string, ColumnBinding> RowSchema =
        ColumnBindingHelper.BuildFromProperties(typeof(PooledAugmentOption));

    /// <summary>Schema snapshot for <c>MithrilQueryBox</c> to drive completion + highlighting.</summary>
    public static IReadOnlyList<ColumnSchema> SchemaSnapshot { get; } =
        ColumnBindingHelper.ToSchema(RowSchema);

    private readonly IReferenceDataService _refData;
    private List<PooledAugmentOption> _allOptions = [];
    private int _totalPowerCount;
    private bool _loaded;

    public AugmentPoolViewModel(
        string sourceLabel,
        string profileName,
        int? minTier,
        int? maxTier,
        string? recommendedSkill,
        int? craftingTargetLevel,
        int? rolledRarityRank,
        string? sourceEquipSlot,
        IReferenceDataService refData,
        string? itemName = null)
    {
        SourceLabel = sourceLabel;
        ProfileName = profileName;
        MinTier = minTier;
        MaxTier = maxTier;
        RecommendedSkill = recommendedSkill;
        CraftingTargetLevel = craftingTargetLevel;
        RolledRarityRank = rolledRarityRank;
        SourceEquipSlot = sourceEquipSlot;
        ItemName = itemName;
        _refData = refData;
        QueryText = BuildInitialQuery(minTier, maxTier, recommendedSkill, craftingTargetLevel, rolledRarityRank, sourceEquipSlot);

        Subtitle = (minTier, maxTier) switch
        {
            (null, null) => $"profile '{profileName}' · expanding…",
            _ => $"profile '{profileName}' · level {minTier}-{maxTier} · expanding…",
        };

        LoadingTask = LoadAsync();
    }

    /// <summary>Test/back-compat overload that omits the contextual hints.</summary>
    public AugmentPoolViewModel(string sourceLabel, string profileName, int? minTier, int? maxTier, IReferenceDataService refData)
        : this(sourceLabel, profileName, minTier, maxTier, recommendedSkill: null, craftingTargetLevel: null, rolledRarityRank: null, sourceEquipSlot: null, refData) { }

    /// <summary>Test/back-compat overload that omits the gear-level context.</summary>
    public AugmentPoolViewModel(string sourceLabel, string profileName, int? minTier, int? maxTier, string? recommendedSkill, IReferenceDataService refData)
        : this(sourceLabel, profileName, minTier, maxTier, recommendedSkill, craftingTargetLevel: null, rolledRarityRank: null, sourceEquipSlot: null, refData) { }

    /// <summary>Test/back-compat overload that omits the rarity context.</summary>
    public AugmentPoolViewModel(string sourceLabel, string profileName, int? minTier, int? maxTier, string? recommendedSkill, int? craftingTargetLevel, IReferenceDataService refData)
        : this(sourceLabel, profileName, minTier, maxTier, recommendedSkill, craftingTargetLevel, rolledRarityRank: null, sourceEquipSlot: null, refData) { }

    /// <summary>Test/back-compat overload that omits the equip-slot context.</summary>
    public AugmentPoolViewModel(string sourceLabel, string profileName, int? minTier, int? maxTier, string? recommendedSkill, int? craftingTargetLevel, int? rolledRarityRank, IReferenceDataService refData)
        : this(sourceLabel, profileName, minTier, maxTier, recommendedSkill, craftingTargetLevel, rolledRarityRank, sourceEquipSlot: null, refData) { }

    public string SourceLabel { get; }
    public string ProfileName { get; }
    public int? MinTier { get; }
    public int? MaxTier { get; }
    public string? RecommendedSkill { get; }
    public int? CraftingTargetLevel { get; }
    public int? RolledRarityRank { get; }
    public string? SourceEquipSlot { get; }
    public string? ItemName { get; }

    /// <summary>Awaitable handle for tests; the constructor kicks off expansion eagerly.</summary>
    public Task LoadingTask { get; }

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>Bound to the card list. One entry per power (post-filter), tiers sorted ascending.</summary>
    public ObservableCollection<GroupedAugmentOption> Groups { get; } = new();

    /// <summary>
    /// Two-way bound to <c>MithrilQueryBox.QueryText</c>. Pre-populated with a tier bracket
    /// when the recipe is an extraction; left empty for enchantments. Changes trigger a re-filter.
    /// </summary>
    [ObservableProperty]
    private string _queryText = "";

    /// <summary>Last filter compile error, surfaced as muted text under the query box.</summary>
    [ObservableProperty]
    private string? _queryError;

    partial void OnQueryTextChanged(string value)
    {
        if (_loaded) RecomputeGroups();
    }

    /// <summary>
    /// Builds an initial query combining: the recipe's tier bracket (Extract recipes only),
    /// the source item's gear level (matched against per-tier MinLevel/MaxLevel windows),
    /// the source item's dominant skill (form gate from the recipe's arg3), the rolled
    /// rarity floor (Uncommon for base enchant, Rare for Max-Enchanting), and the source
    /// item's equip slot (PowerEntry.Slots gate — issue #8). Each clause is independently
    /// optional; the user can edit/clear freely.
    /// </summary>
    private static string BuildInitialQuery(int? minTier, int? maxTier, string? recommendedSkill, int? craftingTargetLevel, int? rolledRarityRank, string? sourceEquipSlot)
    {
        var parts = new List<string>(7);
        if (minTier.HasValue) parts.Add($"Tier >= {minTier.Value}");
        if (maxTier.HasValue) parts.Add($"Tier <= {maxTier.Value}");
        if (craftingTargetLevel.HasValue)
        {
            // A tier rolls when MinLevel ≤ gearLevel ≤ MaxLevel, so the inverse holds:
            // MinLevel must be at-most gearLevel AND MaxLevel must be at-least gearLevel.
            parts.Add($"MinLevel <= {craftingTargetLevel.Value}");
            parts.Add($"MaxLevel >= {craftingTargetLevel.Value}");
        }
        if (rolledRarityRank.HasValue && rolledRarityRank.Value > 0)
        {
            // Tier eligible iff its MinRarityRank ≤ rolled rarity rank.
            parts.Add($"MinRarityRank <= {rolledRarityRank.Value}");
        }
        if (!string.IsNullOrEmpty(recommendedSkill)) parts.Add($"Skill = \"{recommendedSkill}\"");
        if (!string.IsNullOrEmpty(sourceEquipSlot)) parts.Add($"Slots contains \"{sourceEquipSlot}\"");
        return parts.Count == 0 ? "" : string.Join(" AND ", parts);
    }

    private async Task LoadAsync()
    {
        var profileName = ProfileName;
        var refData = _refData;

        // Heavy: walks the profile, renders EffectDescs for every (power, tier).
        // Reference data is immutable from a single ReferenceDataService swap, so
        // reading from the background thread is safe.
        var allOptions = await Task.Run(() => ExpandProfile(profileName, refData)).ConfigureAwait(true);

        void Apply()
        {
            _allOptions = allOptions;
            _totalPowerCount = allOptions.Select(o => o.PowerInternalName).Distinct(StringComparer.Ordinal).Count();
            _loaded = true;
            RecomputeGroups();
            IsLoading = false;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            await dispatcher.InvokeAsync(Apply);
    }

    /// <summary>
    /// Applies the current <see cref="QueryText"/> as a per-tier predicate, then groups
    /// surviving tiers by (power, suffix, skill) into one card per power.
    /// </summary>
    private void RecomputeGroups()
    {
        var predicate = CompilePredicate(QueryText);
        var filtered = predicate is null ? _allOptions : _allOptions.Where(o => predicate(o)).ToList();

        var grouped = filtered
            .GroupBy(o => new GroupKey(o.PowerInternalName, o.Suffix, o.Skill))
            .Select(g => new GroupedAugmentOption(
                g.Key.Power,
                g.Key.Suffix,
                g.Key.Skill,
                g.OrderBy(t => t.Tier).ToList(),
                ItemName))
            .OrderBy(g => g.Skill, StringComparer.Ordinal)
            .ThenBy(g => g.Suffix ?? g.PowerInternalName, StringComparer.Ordinal)
            .ToList();

        Groups.Clear();
        foreach (var g in grouped) Groups.Add(g);

        UpdateSubtitle(filteredTiers: filtered.Count, filteredPowers: grouped.Count);
    }

    private Func<PooledAugmentOption, bool>? CompilePredicate(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            QueryError = null;
            return null;
        }
        try
        {
            var compiled = QueryCompiler.Compile(queryText, RowSchema, caseSensitive: false);
            QueryError = null;
            return compiled is null ? null : item => compiled(item!);
        }
        catch (QueryException qex)
        {
            QueryError = qex.Message;
            // Show everything when the query is malformed — the user sees the error in the highlighted box.
            return null;
        }
    }

    private void UpdateSubtitle(int filteredTiers, int filteredPowers)
    {
        var totalTiers = _allOptions.Count;
        var totalPowers = _totalPowerCount;
        var counts = (filteredTiers == totalTiers && filteredPowers == totalPowers)
            ? $"{totalPowers} powers · {totalTiers} tier rows"
            : $"{filteredPowers} of {totalPowers} powers · {filteredTiers} of {totalTiers} tier rows";
        Subtitle = (MinTier, MaxTier) switch
        {
            (null, null) => $"profile '{ProfileName}' · {counts}",
            _ => $"profile '{ProfileName}' · level {MinTier}-{MaxTier} · {counts}",
        };
    }

    /// <summary>
    /// Expands a profile to one option per (power, tier). Tier filtering is intentionally
    /// not applied here: the query system filters on top, so a user can clear the
    /// pre-populated bracket to inspect tiers outside the recipe's range.
    /// </summary>
    private static List<PooledAugmentOption> ExpandProfile(string profileName, IReferenceDataService refData)
    {
        if (!refData.Profiles.TryGetValue(profileName, out var powerNames)) return [];

        var options = new List<PooledAugmentOption>(powerNames.Count);
        foreach (var powerName in powerNames)
        {
            if (!refData.Powers.TryGetValue(powerName, out var power)) continue;
            foreach (var (tierNum, tierEntry) in power.Tiers)
            {
                var lines = EffectDescsRenderer.Render(tierEntry.EffectDescs, refData.Attributes);
                options.Add(new PooledAugmentOption(
                    power.InternalName, power.Suffix, power.Skill, tierNum, lines,
                    MinLevel: tierEntry.MinLevel,
                    MaxLevel: tierEntry.MaxLevel == 0 ? null : tierEntry.MaxLevel,
                    MinRarity: tierEntry.MinRarity,
                    SkillLevelPrereq: tierEntry.SkillLevelPrereq,
                    Slots: power.Slots));
            }
        }
        // Sort: skill, then suffix/internal name, then tier ascending. Stable layout for the user.
        options.Sort((a, b) =>
        {
            var s = string.CompareOrdinal(a.Skill, b.Skill);
            if (s != 0) return s;
            var n = string.CompareOrdinal(a.Suffix ?? a.PowerInternalName, b.Suffix ?? b.PowerInternalName);
            if (n != 0) return n;
            return a.Tier.CompareTo(b.Tier);
        });
        return options;
    }

    private readonly record struct GroupKey(string Power, string? Suffix, string Skill);
}
