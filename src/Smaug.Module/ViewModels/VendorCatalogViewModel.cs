using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Smaug.State;

namespace Smaug.ViewModels;

public sealed class VendorCatalogRow
{
    public required string ItemName { get; init; }
    public required string NpcName { get; init; }
    public required string Area { get; init; }
    public required decimal BaseValue { get; init; }
    public string MinFavorTier { get; init; } = "";
}

public sealed partial class VendorCatalogViewModel : ObservableObject
{
    private readonly VendorCatalogService _catalog;

    [ObservableProperty] private string _statusMessage = "";

    public ObservableCollection<VendorCatalogRow> Rows { get; } = new();

    public VendorCatalogViewModel(VendorCatalogService catalog)
    {
        _catalog = catalog;
        _catalog.CatalogChanged += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        Rows.Clear();
        foreach (var e in _catalog.Entries.OrderBy(e => e.ItemName, StringComparer.OrdinalIgnoreCase))
        {
            Rows.Add(new VendorCatalogRow
            {
                ItemName = e.ItemName,
                NpcName = e.NpcName,
                Area = e.Area,
                BaseValue = e.ItemBaseValue,
                MinFavorTier = e.MinFavorTier ?? "",
            });
        }
        StatusMessage = Rows.Count == 0
            ? "No vendor data loaded — check that sources_items.json is available from CDN."
            : $"{Rows.Count:N0} vendor listings across {_catalog.Entries.Select(e => e.NpcKey).Distinct().Count():N0} NPCs.";
    }
}
