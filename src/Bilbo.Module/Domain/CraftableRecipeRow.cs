namespace Bilbo.Domain;

/// <summary>
/// One row in the Bilbo "Craftable Recipes" grid. All public properties are
/// picked up automatically by GorgonDataGrid's query schema, so users can
/// filter via e.g. <c>MaxCraftable &gt; 0 AND IsKnown AND SkillLevelMet</c>.
/// </summary>
public sealed record CraftableRecipeRow(
    string Name,
    string Skill,
    int SkillLevelReq,
    int? CharacterSkillLevel,
    bool SkillLevelMet,
    bool IsKnown,
    int TimesCompleted,
    int MaxCraftable,
    string Ingredients,
    string MissingIngredients,
    string ResultItem,
    int ResultStackSize,
    int IconId,
    string InternalName);
