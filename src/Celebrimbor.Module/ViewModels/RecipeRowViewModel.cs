using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Wpf;

namespace Celebrimbor.ViewModels;

public sealed partial class RecipeRowViewModel : ObservableObject
{
    private readonly IItemDetailPresenter _itemDetail;

    public RecipeRowViewModel(RecipeEntry recipe, IReferenceDataService refData, IItemDetailPresenter itemDetail)
    {
        _itemDetail = itemDetail;
        Recipe = recipe;
        Ingredients = recipe.Ingredients
            .Select(r => refData.Items.TryGetValue(r.ItemCode, out var item)
                ? new IngredientChip(item.Name, item.IconId, r.StackSize, r.ChanceToConsume, item.InternalName)
                : null)
            .Where(c => c is not null).Select(c => c!)
            .ToList();
        CraftedOutputs = ResultEffectsParser.ParseCraftedGear(recipe.ResultEffects, refData);
        Results = ProjectResults(recipe, refData, CraftedOutputs.Count);
        InspectableItems = BuildInspectable(CraftedOutputs, Results);
    }

    private static IReadOnlyList<IngredientChip> BuildInspectable(
        IReadOnlyList<CraftedGearPreview> craftedOutputs, IReadOnlyList<IngredientChip> results)
    {
        // Yields first (they carry the item's real display name + icon), then any crafted-gear
        // preview whose InternalName didn't already show up in Yields. Crafted-equipment recipes
        // commonly list the same template in both ResultItems and ResultEffects — we want one
        // icon per distinct item, not two.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<IngredientChip>(craftedOutputs.Count + results.Count);
        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.InternalName)) continue;
            if (seen.Add(r.InternalName)) list.Add(r);
        }
        foreach (var cg in craftedOutputs)
        {
            if (string.IsNullOrEmpty(cg.InternalName)) continue;
            if (seen.Add(cg.InternalName))
                list.Add(new IngredientChip(cg.DisplayName, cg.IconId, 1, null, cg.InternalName));
        }
        return list;
    }

    [RelayCommand]
    private void OpenItem(string? internalName)
    {
        if (!string.IsNullOrEmpty(internalName))
            _itemDetail.Show(internalName);
    }

    private static IReadOnlyList<IngredientChip> ProjectResults(
        RecipeEntry recipe, IReferenceDataService refData, int craftedOutputCount)
    {
        var primary = recipe.ResultItems
            .Select(r => refData.Items.TryGetValue(r.ItemCode, out var item)
                ? new IngredientChip(item.Name, item.IconId, r.StackSize, null, item.InternalName)
                : null)
            .Where(c => c is not null).Select(c => c!)
            .ToList();
        if (primary.Count > 0) return primary;

        // Crafted-equipment recipes stash their output in ProtoResultItems.
        var proto = (recipe.ProtoResultItems ?? [])
            .Select(r => refData.Items.TryGetValue(r.ItemCode, out var item)
                ? new IngredientChip(item.Name, item.IconId, r.StackSize, null, item.InternalName)
                : null)
            .Where(c => c is not null).Select(c => c!)
            .ToList();
        if (proto.Count > 0) return proto;

        // Crafted-gear effects render their own section; skip the placeholder to avoid double-rendering.
        if (craftedOutputCount > 0) return [];

        // Last-resort: the recipe's own name/icon so the Yields section never renders blank.
        return [new IngredientChip(recipe.Name, recipe.IconId, 1, null)];
    }

    public RecipeEntry Recipe { get; }
    public IReadOnlyList<IngredientChip> Ingredients { get; }
    public IReadOnlyList<IngredientChip> Results { get; }
    public IReadOnlyList<CraftedGearPreview> CraftedOutputs { get; }

    /// <summary>
    /// Every item a user might want to open in the detail window — crafted-gear previews first,
    /// then any <see cref="Results"/> chip whose internal name resolved. Views render this as a
    /// popover list when the count is &gt; 1, or inspect the single entry directly when count == 1.
    /// </summary>
    public IReadOnlyList<IngredientChip> InspectableItems { get; } = [];

    public bool HasInspectableItem => InspectableItems.Count > 0;
    public bool HasMultipleInspectableItems => InspectableItems.Count > 1;

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
