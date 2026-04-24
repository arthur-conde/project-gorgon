using CommunityToolkit.Mvvm.ComponentModel;
using Gorgon.Shared.Reference;

namespace Celebrimbor.ViewModels;

public sealed partial class RecipeRowViewModel : ObservableObject
{
    public RecipeRowViewModel(RecipeEntry recipe, IReferenceDataService refData)
    {
        Recipe = recipe;
        Ingredients = recipe.Ingredients
            .Select(r => refData.Items.TryGetValue(r.ItemCode, out var item)
                ? new IngredientChip(item.Name, item.IconId, r.StackSize, r.ChanceToConsume)
                : null)
            .Where(c => c is not null).Select(c => c!)
            .ToList();
        Results = ProjectResults(recipe, refData);
    }

    private static IReadOnlyList<IngredientChip> ProjectResults(RecipeEntry recipe, IReferenceDataService refData)
    {
        var primary = recipe.ResultItems
            .Select(r => refData.Items.TryGetValue(r.ItemCode, out var item)
                ? new IngredientChip(item.Name, item.IconId, r.StackSize, null)
                : null)
            .Where(c => c is not null).Select(c => c!)
            .ToList();
        if (primary.Count > 0) return primary;

        // Crafted-equipment recipes stash their output in ProtoResultItems.
        var proto = (recipe.ProtoResultItems ?? [])
            .Select(r => refData.Items.TryGetValue(r.ItemCode, out var item)
                ? new IngredientChip(item.Name, item.IconId, r.StackSize, null)
                : null)
            .Where(c => c is not null).Select(c => c!)
            .ToList();
        if (proto.Count > 0) return proto;

        // Last-resort: the recipe's own name/icon so the Yields section never renders blank.
        return [new IngredientChip(recipe.Name, recipe.IconId, 1, null)];
    }

    public RecipeEntry Recipe { get; }
    public IReadOnlyList<IngredientChip> Ingredients { get; }
    public IReadOnlyList<IngredientChip> Results { get; }

    public string Name => Recipe.Name;
    public string InternalName => Recipe.InternalName;
    public int IconId => Recipe.IconId;
    public string Skill => Recipe.Skill;
    public int SkillLevelReq => Recipe.SkillLevelReq;
    public string SkillLabel => $"{Recipe.Skill} {Recipe.SkillLevelReq}";

    [ObservableProperty]
    private int _quantity;

    [ObservableProperty]
    private bool _isKnown;

    [ObservableProperty]
    private bool _meetsSkill = true;

    public bool IsInList => Quantity > 0;

    partial void OnQuantityChanged(int value) => OnPropertyChanged(nameof(IsInList));
}
