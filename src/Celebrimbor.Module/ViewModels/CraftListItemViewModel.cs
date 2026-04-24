using CommunityToolkit.Mvvm.ComponentModel;
using Gorgon.Shared.Reference;

namespace Celebrimbor.ViewModels;

/// <summary>
/// Drawer / recap projection of a single craft-list entry — resolved against
/// reference data so views have display name, icon, and enough recipe metadata
/// to render a hover tooltip without touching IReferenceDataService.
/// </summary>
public sealed partial class CraftListItemViewModel : ObservableObject
{
    public CraftListItemViewModel(
        string recipeInternalName,
        string displayName,
        int iconId,
        int quantity,
        string skill,
        int skillLevelReq,
        IReadOnlyList<IngredientChip> ingredients,
        IReadOnlyList<IngredientChip> results,
        IReadOnlyList<CraftedGearPreview> craftedOutputs)
    {
        RecipeInternalName = recipeInternalName;
        DisplayName = displayName;
        IconId = iconId;
        _quantity = quantity;
        Skill = skill;
        SkillLevelReq = skillLevelReq;
        Ingredients = ingredients;
        Results = results;
        CraftedOutputs = craftedOutputs;
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

    [ObservableProperty]
    private int _quantity;
}
