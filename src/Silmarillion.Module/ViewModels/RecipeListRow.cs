using Mithril.Reference.Models.Recipes;

namespace Silmarillion.ViewModels;

/// <summary>
/// Lightweight projection of a <see cref="Recipe"/> for the Recipes tab card list. Pre-resolves
/// the skill-key internal name to a human-readable <see cref="SkillDisplayName"/> via
/// <c>IReferenceDataService.Skills</c>, and resolves a fallback <see cref="IconId"/> from the
/// recipe's first result item when the recipe itself ships <c>IconId = 0</c>. Card template
/// binds directly to these projected fields. <see cref="IngredientKeywords"/> is the flat,
/// deduplicated list of every keyword string across the recipe's <see cref="RecipeKeywordIngredient"/>
/// slots — queryable via the engine's collection-<c>CONTAINS</c> path (powers the item-detail
/// "Used as" chip deep-link).
/// </summary>
public sealed record RecipeListRow(
    Recipe Recipe,
    string Name,
    int IconId,
    string? SkillDisplayName,
    int SkillLevelReq,
    IReadOnlyList<IngredientKeywordValue> IngredientKeywords);
