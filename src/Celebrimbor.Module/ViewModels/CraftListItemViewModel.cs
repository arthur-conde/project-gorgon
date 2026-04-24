using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gorgon.Shared.Reference;
using Gorgon.Shared.Wpf;

namespace Celebrimbor.ViewModels;

/// <summary>
/// Drawer / recap projection of a single craft-list entry — resolved against
/// reference data so views have display name, icon, and enough recipe metadata
/// to render a hover tooltip without touching IReferenceDataService.
/// </summary>
public sealed partial class CraftListItemViewModel : ObservableObject
{
    private readonly IItemDetailPresenter _itemDetail;

    public CraftListItemViewModel(
        string recipeInternalName,
        string displayName,
        int iconId,
        int quantity,
        string skill,
        int skillLevelReq,
        IReadOnlyList<IngredientChip> ingredients,
        IReadOnlyList<IngredientChip> results,
        IReadOnlyList<CraftedGearPreview> craftedOutputs,
        IItemDetailPresenter itemDetail)
    {
        _itemDetail = itemDetail;
        RecipeInternalName = recipeInternalName;
        DisplayName = displayName;
        IconId = iconId;
        _quantity = quantity;
        Skill = skill;
        SkillLevelReq = skillLevelReq;
        Ingredients = ingredients;
        Results = results;
        CraftedOutputs = craftedOutputs;

        // Yields first, crafted-gear previews that weren't already in Yields next — dedupes
        // the common case where a recipe has both a ResultItems entry and a TSysCraftedEquipment
        // effect pointing at the same template.
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
        InspectableItems = list;
    }

    [RelayCommand]
    private void OpenItem(string? internalName)
    {
        if (!string.IsNullOrEmpty(internalName))
            _itemDetail.Show(internalName);
    }

    public string RecipeInternalName { get; }
    public string DisplayName { get; }
    /// <summary>Alias for <see cref="DisplayName"/> so shared templates keyed by "Name" (RecipeRowViewModel) work here too.</summary>
    public string Name => DisplayName;
    public int IconId { get; }
    public string Skill { get; }
    public int SkillLevelReq { get; }
    public IReadOnlyList<IngredientChip> Ingredients { get; }
    public IReadOnlyList<IngredientChip> Results { get; }
    public IReadOnlyList<CraftedGearPreview> CraftedOutputs { get; }
    public IReadOnlyList<IngredientChip> InspectableItems { get; }

    public bool HasInspectableItem => InspectableItems.Count > 0;
    public bool HasMultipleInspectableItems => InspectableItems.Count > 1;

    [ObservableProperty]
    private int _quantity;
}
