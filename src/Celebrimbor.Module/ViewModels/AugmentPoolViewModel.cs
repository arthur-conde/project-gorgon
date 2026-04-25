using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Shared.Reference;

namespace Celebrimbor.ViewModels;

/// <summary>
/// Viewer for the contents of a single <c>tsysprofiles</c> pool. The expensive
/// <see cref="EffectDescsRenderer"/> work runs on a background thread so the window
/// paints immediately with a "Loading…" placeholder, then swaps in the option list
/// when expansion completes.
///
/// Filtering is delegated to the shared <c>MithrilDataGrid</c> + <c>MithrilQueryBox</c>
/// query system. When the recipe carries a level/tier bracket (the
/// <c>ExtractTSysPower</c> shape), <see cref="QueryText"/> is pre-populated with the
/// equivalent grammar so the default view matches the recipe's mechanic — and the
/// user can edit/clear/widen it to explore the full pool.
/// </summary>
public sealed partial class AugmentPoolViewModel : ObservableObject
{
    private readonly IReferenceDataService _refData;

    public AugmentPoolViewModel(
        string sourceLabel,
        string profileName,
        int? minTier,
        int? maxTier,
        string? recommendedSkill,
        int? craftingTargetLevel,
        int? rolledRarityRank,
        IReferenceDataService refData)
    {
        SourceLabel = sourceLabel;
        ProfileName = profileName;
        MinTier = minTier;
        MaxTier = maxTier;
        RecommendedSkill = recommendedSkill;
        CraftingTargetLevel = craftingTargetLevel;
        RolledRarityRank = rolledRarityRank;
        _refData = refData;
        QueryText = BuildInitialQuery(minTier, maxTier, recommendedSkill, craftingTargetLevel, rolledRarityRank);

        Subtitle = (minTier, maxTier) switch
        {
            (null, null) => $"profile '{profileName}' · expanding…",
            _ => $"profile '{profileName}' · level {minTier}-{maxTier} · expanding…",
        };

        LoadingTask = LoadAsync();
    }

    /// <summary>Test/back-compat overload that omits the contextual hints.</summary>
    public AugmentPoolViewModel(string sourceLabel, string profileName, int? minTier, int? maxTier, IReferenceDataService refData)
        : this(sourceLabel, profileName, minTier, maxTier, recommendedSkill: null, craftingTargetLevel: null, rolledRarityRank: null, refData) { }

    /// <summary>Test/back-compat overload that omits the gear-level context.</summary>
    public AugmentPoolViewModel(string sourceLabel, string profileName, int? minTier, int? maxTier, string? recommendedSkill, IReferenceDataService refData)
        : this(sourceLabel, profileName, minTier, maxTier, recommendedSkill, craftingTargetLevel: null, rolledRarityRank: null, refData) { }

    /// <summary>Test/back-compat overload that omits the rarity context.</summary>
    public AugmentPoolViewModel(string sourceLabel, string profileName, int? minTier, int? maxTier, string? recommendedSkill, int? craftingTargetLevel, IReferenceDataService refData)
        : this(sourceLabel, profileName, minTier, maxTier, recommendedSkill, craftingTargetLevel, rolledRarityRank: null, refData) { }

    public string SourceLabel { get; }
    public string ProfileName { get; }
    public int? MinTier { get; }
    public int? MaxTier { get; }
    public string? RecommendedSkill { get; }
    public int? CraftingTargetLevel { get; }
    public int? RolledRarityRank { get; }

    /// <summary>Awaitable handle for tests; the constructor kicks off expansion eagerly.</summary>
    public Task LoadingTask { get; }

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private ObservableCollection<PooledAugmentOption> _options = new();

    /// <summary>
    /// Two-way bound to <c>MithrilQueryBox.QueryText</c> and <c>MithrilDataGrid.QueryText</c>.
    /// Pre-populated with a tier bracket when the recipe is an extraction; left empty for
    /// enchantments (no recipe-derived constraint to express).
    /// </summary>
    [ObservableProperty]
    private string _queryText = "";

    /// <summary>
    /// Builds an initial query combining: the recipe's tier bracket (Extract recipes only),
    /// the source item's gear level (matched against per-tier MinLevel/MaxLevel windows),
    /// the source item's dominant skill (form gate from the recipe's arg3), and the rolled
    /// rarity floor (Uncommon for base enchant, Rare for Max-Enchanting). Each clause is
    /// independently optional; the user can edit/clear freely.
    /// </summary>
    private static string BuildInitialQuery(int? minTier, int? maxTier, string? recommendedSkill, int? craftingTargetLevel, int? rolledRarityRank)
    {
        var parts = new List<string>(6);
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
            Options = new ObservableCollection<PooledAugmentOption>(allOptions);
            var powerCount = allOptions.Select(o => o.PowerInternalName).Distinct(StringComparer.Ordinal).Count();
            Subtitle = (MinTier, MaxTier) switch
            {
                (null, null) => $"profile '{ProfileName}' · {powerCount} powers · {allOptions.Count} tier rows",
                _ => $"profile '{ProfileName}' · level {MinTier}-{MaxTier} · {powerCount} powers · {allOptions.Count} tier rows",
            };
            IsLoading = false;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            await dispatcher.InvokeAsync(Apply);
    }

    /// <summary>
    /// Expands a profile to one option per (power, tier). Tier filtering is intentionally
    /// not applied here: the grid's query system filters on top, so a user can clear the
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
                    SkillLevelPrereq: tierEntry.SkillLevelPrereq));
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
}
