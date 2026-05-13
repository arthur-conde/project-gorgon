using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Items master-detail view-model. Left side: filterable card list of every item in the
/// reference catalogue. Right side: <see cref="ItemDetailViewModel"/> for the current
/// <see cref="SelectedItem"/>, built lazily on selection change with cross-link sections
/// populated from <c>IReferenceDataService.RecipesByProducedItem</c> /
/// <c>RecipesByIngredientItem</c> / <c>ItemSources</c>.
///
/// Subscribes to <see cref="IReferenceDataService.FileUpdated"/> for <c>"items"</c> and
/// rebuilds <see cref="AllItems"/> on the UI thread, preserving the current selection by
/// <see cref="Item.InternalName"/>. Without this, a background CDN refresh after the tab
/// is loaded would leave WPF's ListBox bound to a stale Item collection while refData
/// hands out new instances — selections set via the navigator would silently fall out of
/// the list.
/// </summary>
public sealed partial class ItemsTabViewModel : ObservableObject
{
    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public ItemsTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator)
    {
        _refData = refData;
        _navigator = navigator;
        _openEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        _allItems = BuildAllItems(refData);
        refData.FileUpdated += OnFileUpdated;
    }

    [ObservableProperty]
    private IReadOnlyList<Item> _allItems;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private Item? _selectedItem;

    [ObservableProperty]
    private ItemDetailViewModel? _detailViewModel;

    partial void OnSelectedItemChanged(Item? value)
    {
        if (value is null)
        {
            DetailViewModel = null;
            return;
        }
        var context = BuildCrossLinkContext(value);
        DetailViewModel = new ItemDetailViewModel(
            value, _refData, context, poolPresenter: null, openEntityCommand: _openEntityCommand);
    }

    private void OnFileUpdated(object? sender, string fileKey)
    {
        // Items.json is the primary trigger. Recipes.json updates also rebuild
        // refData's cross-link indices (BuildRecipeCrossLinkIndices runs on both),
        // which means an open ItemDetailView's "Produced by" / "Used in" chips can
        // go stale even when items.json itself didn't change. Re-resolve on both.
        if (fileKey != "items" && fileKey != "recipes") return;

        UiThread.Run(() =>
        {
            var captured = SelectedItem?.InternalName;
            // items.json refresh rebuilds the bound list. recipes.json refresh
            // only changes the cross-link side; AllItems doesn't need rebuilding.
            if (fileKey == "items")
            {
                AllItems = BuildAllItems(_refData);
            }
            if (!string.IsNullOrEmpty(captured))
            {
                var resolved = AllItems.FirstOrDefault(i => i.InternalName == captured);
                // Force a SelectedItem change so OnSelectedItemChanged rebuilds the
                // detail VM with the fresh refData snapshot. Toggling through null is
                // necessary because [ObservableProperty]'s equality check would
                // suppress a same-reference reassignment.
                SelectedItem = null;
                SelectedItem = resolved;
            }
        });
    }

    private static IReadOnlyList<Item> BuildAllItems(IReferenceDataService refData) =>
        refData.ItemsByInternalName.Values
            .OrderBy(i => i.Name ?? i.InternalName ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

    private ItemDetailContext BuildCrossLinkContext(Item item)
    {
        if (string.IsNullOrEmpty(item.InternalName))
        {
            return ItemDetailContext.Empty;
        }
        return new ItemDetailContext(
            ProducedByRecipes: BuildRecipeChips(_refData.RecipesByProducedItem, item.InternalName!),
            ConsumedByRecipes: BuildRecipeChips(_refData.RecipesByIngredientItem, item.InternalName!),
            Sources: BuildSourceChips(item.InternalName!));
    }

    private IReadOnlyList<EntityChipVm>? BuildRecipeChips(
        IReadOnlyDictionary<string, IReadOnlyList<Recipe>> index,
        string itemInternalName)
    {
        if (!index.TryGetValue(itemInternalName, out var recipes) || recipes.Count == 0)
        {
            return null;
        }
        return recipes
            .OrderBy(r => r.Name ?? r.InternalName ?? r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(r => new EntityChipVm(
                DisplayName: r.Name ?? r.InternalName ?? r.Key,
                IconId: r.IconId > 0 ? r.IconId : ResolveRecipeFallbackIcon(r),
                Reference: EntityRef.Recipe(r.InternalName ?? r.Key),
                IsNavigable: _navigator.CanOpen(EntityRef.Recipe(r.InternalName ?? r.Key))))
            .ToList();
    }

    /// <summary>
    /// Many recipes in the bundled JSON ship with IconId = 0; their visual identity is
    /// carried by the result item's icon. Walk ResultItems → ProtoResultItems → 0 for
    /// the chip-icon fallback so cross-link chips have something to render.
    /// </summary>
    private int ResolveRecipeFallbackIcon(Mithril.Reference.Models.Recipes.Recipe recipe)
    {
        var source = (recipe.ResultItems is { Count: > 0 } ? recipe.ResultItems : recipe.ProtoResultItems)
            ?? (IReadOnlyList<Mithril.Reference.Models.Recipes.RecipeResultItem>)Array.Empty<Mithril.Reference.Models.Recipes.RecipeResultItem>();
        foreach (var result in source)
        {
            if (_refData.Items.TryGetValue(result.ItemCode, out var item) && item.IconId > 0)
                return item.IconId;
        }
        return 0;
    }

    private IReadOnlyList<ItemSourceChipVm>? BuildSourceChips(string itemInternalName)
    {
        if (!_refData.ItemSources.TryGetValue(itemInternalName, out var sources) || sources.Count == 0)
        {
            return null;
        }
        return sources
            .Select(s => new ItemSourceChipVm(
                DisplayName: FormatSourceDisplayName(s),
                Detail: s.Context,
                IconId: null,
                EntityReference: null,
                IsNavigable: false))
            .ToList();
    }

    private static string FormatSourceDisplayName(ItemSource s) =>
        string.IsNullOrEmpty(s.Npc) ? s.Type : $"{s.Type}: {s.Npc}";
}
