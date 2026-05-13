using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Recipes master-detail view-model. Mirrors <see cref="ItemsTabViewModel"/>'s shape:
/// filterable row list on the left, recipe detail on the right. On selection change
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
            .Select(r => new RecipeListRow(
                Recipe: r,
                Name: r.Name ?? r.InternalName ?? r.Key,
                IconId: r.IconId > 0 ? r.IconId : ResolveResultIcon(r),
                SkillDisplayName: ResolveSkillDisplayName(r.Skill),
                SkillLevelReq: r.SkillLevelReq))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<RecipeListRow> AllRecipes { get; }

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _queryError;

    /// <summary>
    /// ListBox-bound selection. Setting it from a Recipe POCO (in tests or via the navigator)
    /// resolves to the matching row.
    /// </summary>
    [ObservableProperty]
    private RecipeListRow? _selectedRow;

    /// <summary>
    /// Convenience accessor — the actual <see cref="Recipe"/> behind the selected row.
    /// Setter resolves the recipe to its row in <see cref="AllRecipes"/>. Tests and the
    /// navigator's OnNavigated handler write through this property.
    /// </summary>
    public Recipe? SelectedRecipe
    {
        get => SelectedRow?.Recipe;
        set => SelectedRow = value is null
            ? null
            : AllRecipes.FirstOrDefault(row => ReferenceEquals(row.Recipe, value));
    }

    [ObservableProperty]
    private RecipeDetailViewModel? _detailViewModel;

    partial void OnSelectedRowChanged(RecipeListRow? value)
    {
        OnPropertyChanged(nameof(SelectedRecipe));
        if (value is null)
        {
            DetailViewModel = null;
            return;
        }

        var recipe = value.Recipe;
        var ingredients = BuildIngredientChips(recipe);
        var produced = BuildProducedChips(recipe);
        var effects = recipe.ResultEffects ?? Array.Empty<string>();
        DetailViewModel = new RecipeDetailViewModel(
            recipe, ingredients, produced, effects, _openEntityCommand, value.SkillDisplayName);
    }

    private string? ResolveSkillDisplayName(string? skillKey) =>
        !string.IsNullOrEmpty(skillKey) && _refData.Skills.TryGetValue(skillKey, out var s)
            ? s.DisplayName
            : skillKey;

    private int ResolveResultIcon(Recipe recipe)
    {
        var source = (recipe.ResultItems is { Count: > 0 } ? recipe.ResultItems : recipe.ProtoResultItems)
            ?? (IReadOnlyList<RecipeResultItem>)Array.Empty<RecipeResultItem>();
        foreach (var result in source)
        {
            if (_refData.Items.TryGetValue(result.ItemCode, out var item) && item.IconId > 0)
                return item.IconId;
        }
        return 0;
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
