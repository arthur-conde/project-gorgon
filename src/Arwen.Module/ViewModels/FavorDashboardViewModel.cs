using System.Collections.ObjectModel;
using Arwen.Domain;
using Arwen.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Character;
using Gorgon.Shared.Reference;

namespace Arwen.ViewModels;

public sealed partial class FavorDashboardViewModel : ObservableObject
{
    private readonly FavorStateService _state;
    private readonly ICharacterDataService _charData;
    private IReadOnlyList<NpcFavorEntry> _allEntries = [];

    public FavorDashboardViewModel(FavorStateService state, ICharacterDataService charData)
    {
        _state = state;
        _charData = charData;
        _state.StateChanged += (_, _) => RefreshList();
        RefreshList();
    }

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedArea = "All";

    [ObservableProperty]
    private ObservableCollection<string> _availableAreas = ["All"];

    [ObservableProperty]
    private ObservableCollection<NpcFavorEntry> _filteredNpcs = [];

    [ObservableProperty]
    private string _statusMessage = "Loading NPC data…";

    [ObservableProperty]
    private bool _showKnownOnly = true;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedAreaChanged(string value) => ApplyFilter();
    partial void OnShowKnownOnlyChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private void Refresh() => _state.Rebuild();

    private void RefreshList()
    {
        _allEntries = _state.Entries;

        var areas = _allEntries
            .Where(e => !string.IsNullOrEmpty(e.Area))
            .Select(e => e.Area)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
        areas.Insert(0, "All");
        AvailableAreas = new ObservableCollection<string>(areas);

        if (!areas.Contains(SelectedArea))
            SelectedArea = "All";

        ApplyFilter();
    }

    public void ApplyFilter()
    {
        IEnumerable<NpcFavorEntry> items = _allEntries;

        if (ShowKnownOnly)
            items = items.Where(e => e.IsKnown);

        if (!string.Equals(SelectedArea, "All", StringComparison.OrdinalIgnoreCase))
            items = items.Where(e => e.Area.Equals(SelectedArea, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText;
            items = items.Where(e =>
                e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Area.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        // Sort: highest tier first, then name
        var sorted = items
            .OrderByDescending(e => (int)e.CurrentTier)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FilteredNpcs = new ObservableCollection<NpcFavorEntry>(sorted);
        StatusMessage = $"Showing {sorted.Count:N0} of {_allEntries.Count:N0} NPCs";
    }
}
