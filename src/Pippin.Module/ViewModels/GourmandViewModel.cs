using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Gorgon.Shared.Character;
using Pippin.Domain;
using Pippin.State;

namespace Pippin.ViewModels;

public sealed partial class GourmandViewModel : ObservableObject
{
    private readonly GourmandStateMachine _state;
    private readonly FoodCatalog _catalog;
    private readonly IActiveCharacterService? _activeChar;
    private readonly ICollectionView _foodsView;

    public GourmandViewModel(
        GourmandStateMachine state,
        FoodCatalog catalog,
        IActiveCharacterService? characterData = null)
    {
        _state = state;
        _catalog = catalog;
        _activeChar = characterData;

        Foods = new ObservableCollection<FoodItemViewModel>();
        _foodsView = CollectionViewSource.GetDefaultView(Foods);
        // Grid composes its QueryText predicate on top of this combo-level filter.
        _foodsView.Filter = PassesComboFilters;

        _state.StateChanged += (_, _) => Rebuild();
        if (_activeChar is not null)
        {
            _activeChar.ActiveCharacterChanged += (_, _) => OnPropertyChanged(nameof(GourmandLevel));
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
            if (_activeChar is null) return 0;
            foreach (var c in _activeChar.Characters)
            {
                if (c.Skills.TryGetValue("Gourmand", out var skill))
                    return skill.Level;
            }
            return 0;
        }
    }

    partial void OnFoodTypeFilterChanged(string value) => _foodsView.Refresh();
    partial void OnEatenFilterChanged(string value) => _foodsView.Refresh();

    private void Rebuild()
    {
        var eaten = _state.EatenFoods;

        // Build the full list off the bound collection, then swap in a single Reset
        // to avoid per-item CollectionChanged notifications on large catalogs.
        var list = new List<FoodItemViewModel>(_catalog.TotalCount + eaten.Count);
        foreach (var food in _catalog.ByName.Values)
        {
            var isEaten = eaten.TryGetValue(food.Name, out var count);
            list.Add(new FoodItemViewModel(food, isEaten, isEaten ? count : 0));
        }
        foreach (var (name, count) in eaten)
        {
            if (!_catalog.ByName.ContainsKey(name))
                list.Add(new FoodItemViewModel(name, count));
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        Foods.Clear();
        foreach (var vm in list)
            Foods.Add(vm);
        _foodsView.Refresh();

        EatenCount = eaten.Count;
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
}
