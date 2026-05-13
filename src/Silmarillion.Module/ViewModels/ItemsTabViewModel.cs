using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Reference.Models.Items;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Items master-detail view-model. Left side: filterable card list of every item in the
/// reference catalogue. Right side: <see cref="ItemDetailViewModel"/> for the current
/// <see cref="SelectedItem"/>, built lazily on selection change. Cross-link population
/// for the detail VM arrives in Phase 9.
/// </summary>
public sealed partial class ItemsTabViewModel : ObservableObject
{
    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;

    public ItemsTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator)
    {
        _refData = refData;
        _navigator = navigator;
        AllItems = refData.ItemsByInternalName.Values
            .OrderBy(i => i.Name ?? i.InternalName ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>All items in the catalogue, ordered for display.</summary>
    public IReadOnlyList<Item> AllItems { get; }

    /// <summary>Filter text fed to <c>QueryFilter</c> attached behaviour on the card list.</summary>
    [ObservableProperty]
    private string _queryText = "";

    /// <summary>Parser-reported filter error string (drives a red error TextBlock under the query box).</summary>
    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private Item? _selectedItem;

    [ObservableProperty]
    private ItemDetailViewModel? _detailViewModel;

    partial void OnSelectedItemChanged(Item? value)
    {
        DetailViewModel = value is null
            ? null
            : new ItemDetailViewModel(value, _refData);
    }
}
