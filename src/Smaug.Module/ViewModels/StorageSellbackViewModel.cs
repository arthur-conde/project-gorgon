using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Smaug.State;

namespace Smaug.ViewModels;

public sealed class StorageSellbackVendorRow
{
    public required string NpcKey { get; init; }
    public required string NpcName { get; init; }
    public required string Area { get; init; }
    public required string MinFavorTier { get; init; }
    public required int DistinctItemCount { get; init; }
    public required int TotalStackCount { get; init; }
    public required decimal TotalStackValue { get; init; }
}

public sealed class StorageSellbackItemRow
{
    public required string ItemName { get; init; }
    public required int StackSize { get; init; }
    public required decimal UnitValue { get; init; }
    public required decimal TotalValue { get; init; }
    public required string Location { get; init; }
}

/// <summary>
/// Master-detail view for cross-referencing the active character's storage against vendor
/// buy filters. Left pane: vendors grouped by Area, each showing total stack value of items
/// they'd accept. Right pane: the buyable items held by the active character.
/// </summary>
public sealed partial class StorageSellbackViewModel : ObservableObject
{
    private readonly StorageSellbackService _service;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private StorageSellbackVendorRow? _selectedVendor;

    public ObservableCollection<StorageSellbackVendorRow> Vendors { get; } = new();
    public ICollectionView VendorsView { get; }
    public ObservableCollection<StorageSellbackItemRow> SelectedVendorItems { get; } = new();

    public StorageSellbackViewModel(StorageSellbackService service)
    {
        _service = service;
        VendorsView = CollectionViewSource.GetDefaultView(Vendors);
        VendorsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StorageSellbackVendorRow.Area)));
        VendorsView.SortDescriptions.Add(new SortDescription(nameof(StorageSellbackVendorRow.Area), ListSortDirection.Ascending));
        // Within area, sort by total value descending so the highest-value vendor surfaces first.
        VendorsView.SortDescriptions.Add(new SortDescription(nameof(StorageSellbackVendorRow.TotalStackValue), ListSortDirection.Descending));

        _service.VendorsChanged += (_, _) => Rebuild();
        Rebuild();
    }

    partial void OnSelectedVendorChanged(StorageSellbackVendorRow? value) => RebuildSelectedItems();

    private void Rebuild()
    {
        var previous = SelectedVendor?.NpcKey;
        Vendors.Clear();
        foreach (var v in _service.Vendors)
        {
            Vendors.Add(new StorageSellbackVendorRow
            {
                NpcKey = v.NpcKey,
                NpcName = v.NpcName,
                Area = v.Area,
                MinFavorTier = v.MinFavorTier ?? "",
                DistinctItemCount = v.DistinctItemCount,
                TotalStackCount = v.TotalStackCount,
                TotalStackValue = v.TotalStackValue,
            });
        }

        StatusMessage = BuildStatus();

        SelectedVendor = previous is null
            ? Vendors.FirstOrDefault()
            : Vendors.FirstOrDefault(v => v.NpcKey == previous) ?? Vendors.FirstOrDefault();
    }

    private void RebuildSelectedItems()
    {
        SelectedVendorItems.Clear();
        if (SelectedVendor is null) return;

        var match = _service.Vendors.FirstOrDefault(v => v.NpcKey == SelectedVendor.NpcKey);
        if (match is null) return;

        foreach (var it in match.Items
            .OrderByDescending(i => i.UnitValue * i.StackSize)
            .ThenBy(i => i.ItemName, StringComparer.OrdinalIgnoreCase))
        {
            SelectedVendorItems.Add(new StorageSellbackItemRow
            {
                ItemName = it.ItemName,
                StackSize = it.StackSize,
                UnitValue = it.UnitValue,
                TotalValue = it.UnitValue * it.StackSize,
                Location = it.Location,
            });
        }
    }

    private string BuildStatus()
    {
        if (_service.ActiveCharacter is null)
            return "No storage export found — run /exportstorage in-game, then restart this tab.";
        if (Vendors.Count == 0)
            return $"No vendor buys anything from {_service.ActiveCharacter}'s storage.";
        var totalValue = Vendors.Sum(v => v.TotalStackValue);
        return $"{_service.ActiveCharacter}'s storage · {Vendors.Count:N0} vendors accept items worth up to {totalValue:N0}c combined.";
    }
}
