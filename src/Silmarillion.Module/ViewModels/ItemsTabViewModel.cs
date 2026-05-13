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
        AllItems = refData.ItemsByInternalName.Values
            .OrderBy(i => i.Name ?? i.InternalName ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<Item> AllItems { get; }

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
                IconId: r.IconId,
                Reference: EntityRef.Recipe(r.InternalName ?? r.Key),
                IsNavigable: _navigator.CanOpen(EntityRef.Recipe(r.InternalName ?? r.Key))))
            .ToList();
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
