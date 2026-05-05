using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Character;
using Mithril.Shared.Icons;
using Mithril.Shared.Wpf.Dialogs;
using Pippin.Domain;
using Pippin.Sharing;
using Pippin.State;

namespace Pippin.ViewModels;

public sealed partial class GourmandViewModel : ObservableObject
{
    private readonly GourmandStateMachine _state;
    private readonly FoodCatalog _catalog;
    private readonly IActiveCharacterService? _activeChar;
    private readonly IDialogService? _dialogs;
    private readonly PippinShareCardRenderer? _shareRenderer;
    private readonly IIconCacheService? _iconCache;
    private readonly ICollectionView _foodsView;

    public GourmandViewModel(
        GourmandStateMachine state,
        FoodCatalog catalog,
        IActiveCharacterService? characterData = null,
        IDialogService? dialogs = null,
        PippinShareCardRenderer? shareRenderer = null,
        IIconCacheService? iconCache = null)
    {
        _state = state;
        _catalog = catalog;
        _activeChar = characterData;
        _dialogs = dialogs;
        _shareRenderer = shareRenderer;
        _iconCache = iconCache;

        Foods = new ObservableCollection<FoodItemViewModel>();
        _foodsView = CollectionViewSource.GetDefaultView(Foods);
        // Grid composes its QueryText predicate on top of this combo-level filter.
        _foodsView.Filter = PassesComboFilters;

        _state.StateChanged += (_, _) => Rebuild();
        _catalog.CatalogChanged += (_, _) => Rebuild();
        if (_activeChar is not null)
        {
            _activeChar.ActiveCharacterChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(GourmandLevel));
                Rebuild();
            };
            _activeChar.CharacterExportsChanged += (_, _) => OnPropertyChanged(nameof(GourmandLevel));
        }
        Rebuild();
    }

    public ObservableCollection<FoodItemViewModel> Foods { get; }

    [ObservableProperty] private string _foodTypeFilter = "All";
    [ObservableProperty] private string _eatenFilter = "All";

    [ObservableProperty] private int _eatenCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private double _completionPercent;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _lastSyncLabel = "Not yet synced";

    public int GourmandLevel
    {
        get
        {
            var active = _activeChar?.ActiveCharacter;
            if (active is null) return 0;
            return active.Skills.TryGetValue("Gourmand", out var skill) ? skill.Level : 0;
        }
    }

    partial void OnFoodTypeFilterChanged(string value) => _foodsView.Refresh();
    partial void OnEatenFilterChanged(string value) => _foodsView.Refresh();

    private void Rebuild()
    {
        var eaten = _state.EatenFoodsByInternalName;
        var unknown = _state.UnknownByName;

        // Build the full list off the bound collection, then swap in a single Reset
        // to avoid per-item CollectionChanged notifications on large catalogs.
        var list = new List<FoodItemViewModel>(_catalog.TotalCount + unknown.Count);
        foreach (var food in _catalog.ByInternalName.Values)
        {
            var isEaten = eaten.TryGetValue(food.InternalName, out var count);
            list.Add(new FoodItemViewModel(food, isEaten, isEaten ? count : 0));
        }
        foreach (var (name, count) in unknown)
            list.Add(new FoodItemViewModel(name, count));
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        Foods.Clear();
        foreach (var vm in list)
            Foods.Add(vm);
        _foodsView.Refresh();

        EatenCount = _state.EatenCount;
        TotalCount = _catalog.TotalCount;
        CompletionPercent = TotalCount > 0 ? Math.Round(100.0 * EatenCount / TotalCount, 1) : 0;
        HasData = _state.HasData;

        if (_state.LastReportTime is { } t)
        {
            var ago = DateTimeOffset.UtcNow - t;
            LastSyncLabel = ago.TotalMinutes < 1 ? "Just now"
                : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes}m ago"
                : ago.TotalDays < 1 ? $"{(int)ago.TotalHours}h ago"
                : $"{t.LocalDateTime:g}";
        }
        else
        {
            LastSyncLabel = "Not yet synced";
        }
    }

    private bool PassesComboFilters(object obj)
    {
        if (obj is not FoodItemViewModel vm) return false;

        if (FoodTypeFilter != "All" &&
            !vm.FoodType.Equals(FoodTypeFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (EatenFilter == "Eaten" && !vm.IsEaten) return false;
        if (EatenFilter == "Uneaten" && vm.IsEaten) return false;

        return true;
    }

    [RelayCommand]
    private void ShareProgress()
    {
        if (_dialogs is null || _shareRenderer is null || _iconCache is null) return;
        var catalogIconIds = _catalog.ByInternalName.Values
            .Select(f => f.IconId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var vm = new PippinShareDialogViewModel(
            BuildPayload,
            BuildSummary,
            () => GourmandLevel,
            _shareRenderer,
            _iconCache,
            catalogIconIds,
            _activeChar?.ActiveCharacterName);
        _dialogs.ShowDialog(vm, new PippinShareDialog());
    }

    /// <summary>Snapshot the live state into a <see cref="PippinSharePayload"/>.</summary>
    public PippinSharePayload BuildPayload(bool includeCharacterName)
    {
        var payload = new PippinSharePayload
        {
            CharacterName = includeCharacterName ? _activeChar?.ActiveCharacterName : null,
            EatenFoodsByInternalName = new Dictionary<string, int>(_state.EatenFoodsByInternalName, StringComparer.Ordinal),
            LastReportTime = _state.LastReportTime,
        };
        if (_state.UnknownByName.Count > 0)
            payload.UnknownByName = new Dictionary<string, int>(_state.UnknownByName, StringComparer.OrdinalIgnoreCase);
        return payload;
    }

    /// <summary>
    /// Build a Discord-friendly summary from a snapshot payload, joined against the
    /// sender's catalog so display names (and the top-foods list) reflect what the
    /// sender sees.
    /// </summary>
    public string BuildSummary(PippinSharePayload payload)
    {
        var sb = new StringBuilder();
        var eatenCount = payload.EatenFoodsByInternalName.Count + (payload.UnknownByName?.Count ?? 0);
        var totalCount = _catalog.TotalCount > 0 ? _catalog.TotalCount : eatenCount;
        var pct = totalCount > 0 ? 100.0 * eatenCount / totalCount : 0;

        var prefix = string.IsNullOrEmpty(payload.CharacterName)
            ? "Gourmand"
            : $"{payload.CharacterName} · Gourmand";
        if (GourmandLevel > 0) sb.Append(prefix).Append(" Lv ").Append(GourmandLevel).Append(" · ");
        else sb.Append(prefix).Append(" · ");
        sb.Append(eatenCount).Append(" / ").Append(totalCount)
          .Append(" foods (").Append(pct.ToString("0.#", CultureInfo.InvariantCulture)).AppendLine("%)");

        if (payload.LastReportTime is { } t)
            sb.Append("Last synced: ").AppendLine(FormatAgo(DateTimeOffset.UtcNow - t));

        var top = payload.EatenFoodsByInternalName
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv =>
            {
                var name = _catalog.TryGetByInternalName(kv.Key, out var food) ? food.Name : kv.Key;
                return $"{name} ×{kv.Value}";
            })
            .ToList();
        if (top.Count > 0)
            sb.Append("Top: ").AppendLine(string.Join(", ", top));

        return sb.ToString().TrimEnd();
    }

    private static string FormatAgo(TimeSpan ago)
    {
        if (ago.TotalMinutes < 1) return "just now";
        if (ago.TotalHours < 1) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalDays < 1) return $"{(int)ago.TotalHours}h ago";
        return $"{(int)ago.TotalDays}d ago";
    }
}
