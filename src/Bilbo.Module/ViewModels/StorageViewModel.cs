using System.Collections.ObjectModel;
using System.ComponentModel;
using Bilbo.Domain;
using Bilbo.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Character;
using Gorgon.Shared.Game;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Storage;
using Gorgon.Shared.Wpf;

namespace Bilbo.ViewModels;

public sealed partial class StorageViewModel : ObservableObject
{
    private readonly GameConfig _gameConfig;
    private readonly BilboSettings _settings;
    private readonly IReferenceDataService _refData;
    private readonly IActiveCharacterService _activeChar;
    private IReadOnlyList<StorageItemRow> _allItems = [];

    public StorageViewModel(
        GameConfig gameConfig,
        BilboSettings settings,
        IReferenceDataService refData,
        IActiveCharacterService activeChar)
    {
        _gameConfig = gameConfig;
        _settings = settings;
        _refData = refData;
        _activeChar = activeChar;
        _activeChar.ActiveCharacterChanged += (_, _) => DispatchReload();
        _activeChar.StorageReportsChanged += (_, _) => DispatchReload();
        Reload();
    }

    private void DispatchReload()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Reload();
        else d.InvokeAsync(Reload);
    }

    public DataGridState GridState => _settings.StorageGrid;

    // ── Observable properties ────────────────────────────────────────────

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedLocation = "All";

    [ObservableProperty]
    private ObservableCollection<string> _availableLocations = ["All"];

    [ObservableProperty]
    private IReadOnlyList<StorageItemRow> _filteredItems = [];

    [ObservableProperty]
    private string _statusMessage = "No storage export loaded.";

    public string? ActiveCharacterLabel
    {
        get
        {
            var name = _activeChar.ActiveCharacterName;
            if (string.IsNullOrEmpty(name)) return null;
            var server = _activeChar.ActiveServer;
            return string.IsNullOrEmpty(server) ? name : $"{name} · {server}";
        }
    }

    [ObservableProperty]
    private IReadOnlyList<CraftableRecipeRow> _craftableRecipes = [];

    [ObservableProperty]
    private string _recipeQueryText = "";

    [ObservableProperty]
    private ConfidenceLevel _confidence = ConfidenceLevel.P95;

    public IReadOnlyList<ConfidenceOption> ConfidenceOptions { get; } =
    [
        new("Assume always consumes", ConfidenceLevel.WorstCase),
        new("Median (50%)", ConfidenceLevel.P50),
        new("95% confident", ConfidenceLevel.P95),
        new("99% confident", ConfidenceLevel.P99),
    ];

    public sealed record ConfidenceOption(string Label, ConfidenceLevel Value);

    // ── Property change handlers ─────────────────────────────────────────

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedLocationChanged(string value) => ApplyFilter();

    partial void OnConfidenceChanged(ConfidenceLevel value) => ApplyFilter();

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void Refresh()
    {
        _activeChar.Refresh();
        Reload();
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private void Reload()
    {
        OnPropertyChanged(nameof(ActiveCharacterLabel));

        var report = _activeChar.ActiveStorageContents;
        if (report is null)
        {
            _allItems = [];
            AvailableLocations = new ObservableCollection<string>(["All"]);
            SelectedLocation = "All";
            FilteredItems = [];
            StatusMessage = _activeChar.ActiveCharacterName is null
                ? "No active character — switch one from the shell header."
                : $"No storage export found for {_activeChar.ActiveCharacterName}. Run /exportstorage in-game.";
            CraftableRecipes = [];
            return;
        }

        _allItems = StorageRowMapper.ToRows(report, _refData);

        var locations = _allItems
            .Select(r => r.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();
        locations.Insert(0, "All");
        AvailableLocations = new ObservableCollection<string>(locations);
        SelectedLocation = "All";

        ApplyFilter();
    }

    public void ApplyFilter()
    {
        IEnumerable<StorageItemRow> items = _allItems;

        // Global filters
        if (!string.Equals(SelectedLocation, "All", StringComparison.OrdinalIgnoreCase))
        {
            items = items.Where(i =>
                i.Location.Equals(SelectedLocation, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            items = items.Where(i =>
                i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        // Per-column filters
        foreach (var col in GridState.Columns)
        {
            if (string.IsNullOrEmpty(col.FilterText)) continue;
            var filter = col.FilterText;
            var key = col.Key;
            items = items.Where(row =>
                GetCellText(row, key).Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        // Sort
        var sortCol = GridState.Columns.FirstOrDefault(c => c.SortDirection is not null);
        if (sortCol is not null)
        {
            items = sortCol.SortDirection == "Ascending"
                ? items.OrderBy(r => GetSortValue(r, sortCol.Key))
                : items.OrderByDescending(r => GetSortValue(r, sortCol.Key));
        }

        var result = items.ToList();
        FilteredItems = result;
        StatusMessage = $"Showing {result.Count:N0} of {_allItems.Count:N0} items";

        CraftableRecipes = CraftableRecipeCalculator.Compute(
            result, _refData, _activeChar.ActiveCharacter, Confidence);
    }

    private static string GetCellText(StorageItemRow row, string key) => key switch
    {
        "Name" => row.Name,
        "Location" => row.Location,
        "StackSize" => row.StackSize.ToString(),
        "TotalValue" => row.TotalValue.ToString("N2"),
        "Rarity" => row.Rarity ?? "",
        "Slot" => row.Slot ?? "",
        "Level" => row.Level?.ToString() ?? "",
        "ModCount" => row.ModCount.ToString(),
        _ => "",
    };

    private static IComparable GetSortValue(StorageItemRow row, string key) => key switch
    {
        "Name" => row.Name,
        "Location" => row.Location,
        "StackSize" => row.StackSize,
        "TotalValue" => row.TotalValue,
        "Rarity" => row.Rarity ?? "",
        "Slot" => row.Slot ?? "",
        "Level" => row.Level ?? 0,
        "ModCount" => row.ModCount,
        _ => "",
    };
}
