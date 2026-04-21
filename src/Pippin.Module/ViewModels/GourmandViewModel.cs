using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Gorgon.Shared.Character;
using Pippin.Domain;
using Pippin.State;

namespace Pippin.ViewModels;

public sealed partial class GourmandViewModel : ObservableObject
{
    private readonly GourmandStateMachine _state;
    private readonly FoodCatalog _catalog;
    private readonly ICharacterDataService? _characterData;
    private List<FoodItemViewModel> _allFoods = [];

    public GourmandViewModel(
        GourmandStateMachine state,
        FoodCatalog catalog,
        ICharacterDataService? characterData = null)
    {
        _state = state;
        _catalog = catalog;
        _characterData = characterData;
        _state.StateChanged += (_, _) => Rebuild();
        if (_characterData is not null)
            _characterData.CharactersChanged += (_, _) => OnPropertyChanged(nameof(GourmandLevel));
        Rebuild();
    }

    public ObservableCollection<FoodItemViewModel> Foods { get; } = [];

    [ObservableProperty] private string _searchText = "";
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
            if (_characterData is null) return 0;
            foreach (var c in _characterData.Characters)
            {
                if (c.Skills.TryGetValue("Gourmand", out var skill))
                    return skill.Level;
            }
            return 0;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnFoodTypeFilterChanged(string value) => ApplyFilters();
    partial void OnEatenFilterChanged(string value) => ApplyFilters();

    private void Rebuild()
    {
        var eaten = _state.EatenFoods;
        var list = new List<FoodItemViewModel>(_catalog.TotalCount);

        // Add all catalog foods with eaten status
        foreach (var food in _catalog.ByName.Values)
        {
            var isEaten = eaten.TryGetValue(food.Name, out var count);
            list.Add(new FoodItemViewModel(food, isEaten, isEaten ? count : 0));
        }

        // Add eaten foods not in catalog (edge case: CDN data mismatch)
        foreach (var (name, count) in eaten)
        {
            if (!_catalog.ByName.ContainsKey(name))
                list.Add(new FoodItemViewModel(name, count));
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _allFoods = list;

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

        ApplyFilters();
    }

    private void ApplyFilters()
    {
        Foods.Clear();
        foreach (var vm in _allFoods)
        {
            if (!PassesFilter(vm)) continue;
            Foods.Add(vm);
        }
    }

    private bool PassesFilter(FoodItemViewModel vm)
    {
        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !vm.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            return false;

        if (FoodTypeFilter != "All" &&
            !vm.FoodType.Equals(FoodTypeFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (EatenFilter == "Eaten" && !vm.IsEaten) return false;
        if (EatenFilter == "Uneaten" && vm.IsEaten) return false;

        return true;
    }
}
