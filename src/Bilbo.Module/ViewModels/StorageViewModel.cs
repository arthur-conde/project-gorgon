using System.Collections.ObjectModel;
using System.ComponentModel;
using Bilbo.Domain;
using Bilbo.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Game;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Wpf;

namespace Bilbo.ViewModels;

public sealed partial class StorageViewModel : ObservableObject
{
    private readonly GameConfig _gameConfig;
    private readonly BilboSettings _settings;
    private readonly IReferenceDataService _refData;
    private IReadOnlyList<StorageItemRow> _allItems = [];

    public StorageViewModel(GameConfig gameConfig, BilboSettings settings, IReferenceDataService refData)
    {
        _gameConfig = gameConfig;
        _settings = settings;
        _refData = refData;
        RefreshReports();
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
    private ObservableCollection<ReportFileInfo> _availableReports = [];

    [ObservableProperty]
    private ReportFileInfo? _selectedReport;

    [ObservableProperty]
    private string _statusMessage = "No storage export loaded.";

    // ── Property change handlers ─────────────────────────────────────────

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedLocationChanged(string value) => ApplyFilter();

    partial void OnSelectedReportChanged(ReportFileInfo? value)
    {
        if (value is not null)
            LoadReport(value.FilePath);
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void Refresh()
    {
        RefreshReports();
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private void RefreshReports()
    {
        var dir = _gameConfig.ReportsDirectory;
        var reports = StorageReportLoader.ScanForReports(dir);
        AvailableReports = new ObservableCollection<ReportFileInfo>(reports);

        if (reports.Count > 0)
        {
            SelectedReport = reports[0]; // triggers OnSelectedReportChanged → LoadReport
        }
        else
        {
            _allItems = [];
            AvailableLocations = new ObservableCollection<string>(["All"]);
            SelectedLocation = "All";
            FilteredItems = [];
            StatusMessage = "No storage exports found. Run /exportstorage in-game.";
        }
    }

    private void LoadReport(string filePath)
    {
        try
        {
            var report = StorageReportLoader.Load(filePath);
            _allItems = StorageRowMapper.ToRows(report, _refData);

            // Build location list
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
        catch (Exception ex)
        {
            _allItems = [];
            FilteredItems = [];
            StatusMessage = $"Error loading report: {ex.Message}";
        }
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
