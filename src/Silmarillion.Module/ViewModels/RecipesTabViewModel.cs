using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Recipes master-detail view-model. Mirrors <see cref="ItemsTabViewModel"/>'s shape:
/// filterable card list on the left, recipe detail on the right. On selection change
/// builds a <see cref="RecipeDetailViewModel"/> with ingredient/produced chips
/// resolved from <c>IReferenceDataService</c>.
/// </summary>
public sealed partial class RecipesTabViewModel : ObservableObject
{
    private readonly IReferenceDataService _refData;
    private readonly IReferenceNavigator _navigator;
    private readonly RelayCommand<EntityRef?> _openEntityCommand;

    public RecipesTabViewModel(IReferenceDataService refData, IReferenceNavigator navigator)
    {
        _refData = refData;
        _navigator = navigator;
        _openEntityCommand = new RelayCommand<EntityRef?>(r => { if (r is not null) _navigator.Open(r); });
        AllRecipes = refData.Recipes.Values
            .OrderBy(r => r.Name ?? r.InternalName ?? r.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<Recipe> AllRecipes { get; }

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    [ObservableProperty]
    private Recipe? _selectedRecipe;

    [ObservableProperty]
    private RecipeDetailViewModel? _detailViewModel;

    partial void OnSelectedRecipeChanged(Recipe? value)
    {
        if (value is null)
        {
            DetailViewModel = null;
            return;
        }

        var ingredients = BuildIngredientChips(value);
        var produced = BuildProducedChips(value);
        var effects = value.ResultEffects ?? Array.Empty<string>();
        DetailViewModel = new RecipeDetailViewModel(value, ingredients, produced, effects, _openEntityCommand);
    }

    private IReadOnlyList<EntityChipVm> BuildIngredientChips(Recipe recipe) =>
        (recipe.Ingredients ?? (IReadOnlyList<RecipeIngredient>)Array.Empty<RecipeIngredient>())
            .OfType<RecipeItemIngredient>()
            .Select(ing => BuildItemChip(ing.ItemCode, ing.StackSize, percentChance: null))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

    private IReadOnlyList<EntityChipVm> BuildProducedChips(Recipe recipe)
    {
        var source = (recipe.ResultItems is { Count: > 0 } ? recipe.ResultItems : recipe.ProtoResultItems)
            ?? (IReadOnlyList<RecipeResultItem>)Array.Empty<RecipeResultItem>();
        return source
            .Select(res => BuildItemChip(res.ItemCode, res.StackSize, res.PercentChance))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
    }

    private EntityChipVm? BuildItemChip(long itemCode, int stackSize, double? percentChance)
    {
        if (!_refData.Items.TryGetValue(itemCode, out var item) || string.IsNullOrEmpty(item.InternalName))
            return null;
        var displayName = stackSize > 1
            ? $"{item.Name ?? item.InternalName} ×{stackSize}"
            : item.Name ?? item.InternalName ?? "";
        if (percentChance is { } pc && pc < 100)
        {
            displayName += $" ({pc:0}%)";
        }
        var reference = EntityRef.Item(item.InternalName!);
        return new EntityChipVm(displayName, item.IconId, reference, _navigator.CanOpen(reference));
    }
}
