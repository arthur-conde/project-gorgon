using System.Windows.Input;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// Read-only projection of a <see cref="Recipe"/> for the Silmarillion module's recipe
/// detail pane. Hostable in both the master-detail right pane and the popup
/// <see cref="Silmarillion.Views.RecipeDetailWindow"/>.
/// Cross-link projections (ingredient/result chips) are supplied by the page-level
/// view-model, which has access to <see cref="IReferenceDataService"/> and the navigator's
/// <c>CanOpen</c> for the <see cref="EntityChipVm.IsNavigable"/> flag.
/// </summary>
public sealed class RecipeDetailViewModel
{
    public RecipeDetailViewModel(
        Recipe recipe,
        IReadOnlyList<EntityChipVm> ingredients,
        IReadOnlyList<EntityChipVm> producedItems,
        IReadOnlyList<string> resultEffectsText,
        ICommand? openEntityCommand = null,
        string? skillDisplayName = null)
    {
        Recipe = recipe;
        Ingredients = ingredients;
        ProducedItems = producedItems;
        ResultEffectsText = resultEffectsText;
        OpenEntityCommand = openEntityCommand;
        SkillDisplayName = skillDisplayName ?? recipe.Skill;
    }

    /// <summary>
    /// Human-readable skill name (resolved by the page VM via <c>IReferenceDataService.Skills</c>),
    /// falling back to the internal name if resolution fails. Drives <see cref="SkillRequirementChip"/>.
    /// </summary>
    public string? SkillDisplayName { get; }

    public Recipe Recipe { get; }

    public string DisplayName => Recipe.Name ?? Recipe.InternalName ?? Recipe.Key;
    public string InternalName => Recipe.InternalName ?? "";
    public string? Description => Recipe.Description;
    public string? Skill => Recipe.Skill;
    public int SkillLevelReq => Recipe.SkillLevelReq;
    public int IconId => Recipe.IconId;

    /// <summary>
    /// Combined "Skill Level" chip (e.g. "Cooking 30"). Empty when no skill is set —
    /// caller should hide the chip border on empty strings.
    /// </summary>
    public string SkillRequirementChip =>
        string.IsNullOrEmpty(SkillDisplayName) ? "" : $"{SkillDisplayName} {Recipe.SkillLevelReq}";

    public IReadOnlyList<EntityChipVm> Ingredients { get; }

    public IReadOnlyList<EntityChipVm> ProducedItems { get; }

    /// <summary>
    /// TODO(stub:#214): plain-string rendering of recipe ResultEffects. Replaced by rich
    /// chip templates in #214.
    /// </summary>
    public IReadOnlyList<string> ResultEffectsText { get; }

    /// <summary>
    /// Command invoked when the user clicks an ingredient/produced chip. Receives the chip's
    /// <see cref="EntityRef"/>. Wired by <see cref="RecipesTabViewModel"/> to the navigator.
    /// </summary>
    public ICommand? OpenEntityCommand { get; }
}
