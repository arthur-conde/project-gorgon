using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Elrond.Domain;

public sealed class ElrondSettings : INotifyPropertyChanged
{
    private string _lastSkill = "";
    public string LastSkill
    {
        get => _lastSkill;
        set => Set(ref _lastSkill, value);
    }

    private int? _lastGoalLevel;
    public int? LastGoalLevel
    {
        get => _lastGoalLevel;
        set => Set(ref _lastGoalLevel, value);
    }

    /// <summary>
    /// Active sort keys in precedence order. Each entry is a stable
    /// <see cref="PersistedSortEntry.Id"/> (looked up against the VM's
    /// AvailableSortKeys at load time) plus its current direction.
    /// </summary>
    private List<PersistedSortEntry> _activeSortKeys = [];
    public List<PersistedSortEntry> ActiveSortKeys
    {
        get => _activeSortKeys;
        set => Set(ref _activeSortKeys, value ?? []);
    }

    /// <summary>
    /// Legacy single-key sort field. Read on first launch after upgrade so the
    /// new <see cref="ActiveSortKeys"/> is seeded with the user's previous
    /// choice; ignored thereafter. Deserializer keeps this populated for
    /// backwards compatibility — the migration runs on VM construction.
    /// </summary>
    private string? _legacySortKey;
    public string? SortKey
    {
        get => _legacySortKey;
        set => Set(ref _legacySortKey, value);
    }

    private bool? _legacySortDescending;
    public bool? SortDescending
    {
        get => _legacySortDescending;
        set => Set(ref _legacySortDescending, value);
    }

    /// <summary>
    /// Active filter Ids (resolved against the VM's AvailableFilters at load
    /// time). Stored as plain strings so adding/renaming filters in code is
    /// non-fatal: stale Ids are silently ignored.
    /// </summary>
    private List<string> _activeFilterIds = [];
    public List<string> ActiveFilterIds
    {
        get => _activeFilterIds;
        set => Set(ref _activeFilterIds, value ?? []);
    }

    /// <summary>
    /// Distinguishes "never persisted" from "persisted as empty" — when false
    /// the VM keeps its constructor-declared defaults; when true it mirrors
    /// <see cref="ActiveFilterIds"/> verbatim (including the empty case).
    /// </summary>
    private bool _hasPersistedFilters;
    public bool HasPersistedFilters
    {
        get => _hasPersistedFilters;
        set => Set(ref _hasPersistedFilters, value);
    }

    private string _viewMode = "Rows";
    public string ViewMode
    {
        get => _viewMode;
        set => Set(ref _viewMode, value);
    }

    /// <summary>
    /// Simulator constraint: when true, the leveling simulator may only pick recipes
    /// already in the input snapshot's RecipeCompletions. Pessimistic — assumes the
    /// player won't learn anything new mid-grind. Pre-decoupling default: false.
    /// </summary>
    private bool _simOnlyAlreadyLearnedRecipes;
    public bool SimOnlyAlreadyLearnedRecipes
    {
        get => _simOnlyAlreadyLearnedRecipes;
        set => Set(ref _simOnlyAlreadyLearnedRecipes, value);
    }

    /// <summary>
    /// Simulator constraint: when true (default), first-time-bonus crafts are
    /// prioritised. Toggle off to bank bonuses for a future scenario.
    /// </summary>
    private bool _simUseFirstTimeBonuses = true;
    public bool SimUseFirstTimeBonuses
    {
        get => _simUseFirstTimeBonuses;
        set => Set(ref _simUseFirstTimeBonuses, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// One persisted entry in <see cref="ElrondSettings.ActiveSortKeys"/>.
/// Stored as JSON (id + direction); the VM resolves <paramref name="Id"/> back
/// to a typed <c>SortKey&lt;RecipeAnalysis&gt;</c> at load time.
/// </summary>
public sealed record PersistedSortEntry(string Id, System.ComponentModel.ListSortDirection Direction);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ElrondSettings))]
[JsonSerializable(typeof(PersistedSortEntry))]
[JsonSerializable(typeof(List<PersistedSortEntry>))]
[JsonSerializable(typeof(List<string>))]
public partial class ElrondSettingsJsonContext : JsonSerializerContext { }
