using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Smaug.State;

namespace Smaug.ViewModels;

public sealed class VendorRow
{
    public required string NpcKey { get; init; }
    public required string NpcName { get; init; }
    public required string Area { get; init; }
    public required string MinFavorTier { get; init; }
    public required int ItemCount { get; init; }
}

public sealed class VendorShopItemRow
{
    public required string ItemName { get; init; }
    public required decimal BaseValue { get; init; }
}

/// <summary>
/// Master-detail view over the vendor catalog: left pane lists vendors grouped by Area,
/// right pane shows the currently-selected vendor's inventory.
/// </summary>
public sealed partial class VendorShopViewModel : ObservableObject
{
    private readonly VendorCatalogService _catalog;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private VendorRow? _selectedVendor;

    public ObservableCollection<VendorRow> Vendors { get; } = new();
    public ICollectionView VendorsView { get; }
    public ObservableCollection<VendorShopItemRow> SelectedVendorItems { get; } = new();

    public VendorShopViewModel(VendorCatalogService catalog)
    {
        _catalog = catalog;
        VendorsView = CollectionViewSource.GetDefaultView(Vendors);
        VendorsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(VendorRow.Area)));
        VendorsView.SortDescriptions.Add(new SortDescription(nameof(VendorRow.Area), ListSortDirection.Ascending));
        VendorsView.SortDescriptions.Add(new SortDescription(nameof(VendorRow.NpcName), ListSortDirection.Ascending));

        _catalog.CatalogChanged += (_, _) => RebuildVendors();
        RebuildVendors();
    }

    partial void OnSelectedVendorChanged(VendorRow? value) => RebuildSelectedItems();

    private void RebuildVendors()
    {
        var previousNpc = SelectedVendor?.NpcKey;
        Vendors.Clear();

        var grouped = _catalog.Entries
            .GroupBy(e => e.NpcKey, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                return new VendorRow
                {
                    NpcKey = g.Key,
                    NpcName = first.NpcName,
                    Area = string.IsNullOrEmpty(first.Area) ? "(Unknown Area)" : first.Area,
                    MinFavorTier = first.MinFavorTier ?? "",
                    ItemCount = g.Count(),
                };
            });

        foreach (var v in grouped)
            Vendors.Add(v);

        StatusMessage = Vendors.Count == 0
            ? "No vendor data loaded — check that sources_items.json is available from CDN."
            : $"{Vendors.Count:N0} vendors across {Vendors.Select(v => v.Area).Distinct().Count():N0} areas.";

        SelectedVendor = previousNpc is null
            ? Vendors.FirstOrDefault()
            : Vendors.FirstOrDefault(v => v.NpcKey == previousNpc) ?? Vendors.FirstOrDefault();
    }

    private void RebuildSelectedItems()
    {
        SelectedVendorItems.Clear();
        if (SelectedVendor is null) return;

        var npc = SelectedVendor.NpcKey;
        var items = _catalog.Entries
            .Where(e => string.Equals(e.NpcKey, npc, StringComparison.Ordinal))
            .OrderBy(e => e.ItemName, StringComparer.OrdinalIgnoreCase);

        foreach (var e in items)
        {
            SelectedVendorItems.Add(new VendorShopItemRow
            {
                ItemName = e.ItemName,
                BaseValue = e.ItemBaseValue,
            });
        }
    }
}
