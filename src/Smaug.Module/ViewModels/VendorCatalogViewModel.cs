using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Smaug.State;

namespace Smaug.ViewModels;

public sealed class VendorCatalogRow
{
    public required string ItemName { get; init; }
    public required int IconId { get; init; }
    public required string NpcName { get; init; }
    public required string Area { get; init; }
    public required decimal BaseValue { get; init; }
    public string MinFavorTier { get; init; } = "";
    public string Acceptance { get; init; } = "";
    /// <summary>True when known-acceptable; false when known-over-cap; null when unknown.</summary>
    public bool? IsAcceptable { get; init; }
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
                IconId = e.ItemIconId,
                NpcName = e.NpcName,
                Area = e.Area,
                BaseValue = e.ItemBaseValue,
                MinFavorTier = e.MinFavorTier ?? "",
                IsAcceptable = e.IsAcceptable,
                Acceptance = FormatAcceptance(e),
            });
        }
        StatusMessage = Rows.Count == 0
            ? "No vendor data loaded — check that sources_items.json is available from CDN."
            : $"{Rows.Count:N0} vendor listings across {_catalog.Entries.Select(e => e.NpcKey).Distinct().Count():N0} NPCs.";
    }

    private static string FormatAcceptance(VendorCatalogEntry e)
    {
        if (e.IsAcceptable is null) return "—";
        return e.IsAcceptable.Value
            ? (e.EffectiveMaxGold is not null ? $"≤ {e.EffectiveMaxGold:N0}c" : "OK")
            : $"over {e.EffectiveMaxGold:N0}c cap";
    }
}
