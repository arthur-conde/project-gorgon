using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Smaug.State;

namespace Smaug.ViewModels;

public sealed class SellPlannerItemRow
{
    public required int TypeId { get; init; }
    public required string InternalName { get; init; }
    public required string DisplayName { get; init; }
    public required int IconId { get; init; }
    public required decimal UnitValue { get; init; }
    public required int StackCount { get; init; }
    public required string Location { get; init; }
}

public sealed class SellPlannerVendorRow
{
    public required string NpcKey { get; init; }
    public required string NpcName { get; init; }
    public required string Area { get; init; }
    public required string MinFavorTier { get; init; }
    public required string PlayerFavorTier { get; init; }
    public required bool IsAccessible { get; init; }
    public required string ExpectedPriceText { get; init; }
    public required int SampleCount { get; init; }
    public required string Kind { get; init; }
}

/// <summary>
/// Sell Planner: "I have this item — which vendor pays the most, and do I have access?"
/// Left pane lists items from the active character's storage; right pane lists vendors
/// that accept the item, ordered by expected price. Rows for vendors below the player's
/// current favor tier are marked inaccessible rather than hidden.
/// </summary>
public sealed partial class SellPlannerViewModel : ObservableObject
{
    private readonly SellPlannerService _service;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private SellPlannerItemRow? _selectedItem;
    [ObservableProperty] private string _itemFilter = "";

    public ObservableCollection<SellPlannerItemRow> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<SellPlannerVendorRow> Vendors { get; } = new();

    public SellPlannerViewModel(SellPlannerService service)
    {
        _service = service;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o =>
        {
            if (string.IsNullOrWhiteSpace(ItemFilter)) return true;
            if (o is not SellPlannerItemRow row) return false;
            return row.DisplayName.Contains(ItemFilter, StringComparison.OrdinalIgnoreCase)
                || row.InternalName.Contains(ItemFilter, StringComparison.OrdinalIgnoreCase);
        };

        _service.ItemsChanged += (_, _) => RebuildItems();
        _service.VendorsChanged += (_, _) => RebuildVendors();

        RebuildItems();
    }

    partial void OnSelectedItemChanged(SellPlannerItemRow? value) => RebuildVendors();

    partial void OnItemFilterChanged(string value) => ItemsView.Refresh();

    private void RebuildItems()
    {
        var previous = SelectedItem?.TypeId;
        Items.Clear();
        foreach (var it in _service.OwnedItems)
        {
            Items.Add(new SellPlannerItemRow
            {
                TypeId = it.TypeId,
                InternalName = it.InternalName,
                DisplayName = it.DisplayName,
                IconId = it.IconId,
                UnitValue = it.UnitValue,
                StackCount = it.StackCount,
                Location = it.Location,
            });
        }

        StatusMessage = _service.ActiveCharacterName is null
            ? "No storage export found — run /exportstorage in-game, then restart this tab."
            : Items.Count == 0
                ? $"{_service.ActiveCharacterName}'s storage is empty or unparseable."
                : $"{_service.ActiveCharacterName}'s storage · {Items.Count:N0} distinct items.";

        SelectedItem = previous is null
            ? Items.FirstOrDefault()
            : Items.FirstOrDefault(i => i.TypeId == previous) ?? Items.FirstOrDefault();
    }

    private void RebuildVendors()
    {
        Vendors.Clear();
        if (SelectedItem is null) return;

        var source = _service.OwnedItems.FirstOrDefault(i => i.TypeId == SelectedItem.TypeId);
        if (source is null) return;

        foreach (var v in _service.GetVendorsFor(source))
        {
            var priceText = v.Estimate is null
                ? "—"
                : v.Estimate.Tier == "Ratio"
                    ? $"≈ {v.Estimate.Price:N0}c (ratio)"
                    : $"{v.Estimate.Price:N0}c";

            Vendors.Add(new SellPlannerVendorRow
            {
                NpcKey = v.NpcKey,
                NpcName = v.NpcName,
                Area = v.Area,
                MinFavorTier = v.MinFavorTier ?? "",
                PlayerFavorTier = v.PlayerFavorTier ?? "",
                IsAccessible = v.IsAccessible,
                ExpectedPriceText = priceText,
                SampleCount = v.Estimate?.SampleCount ?? 0,
                Kind = v.Estimate?.Tier ?? "",
            });
        }
    }
}
