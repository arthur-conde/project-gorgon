namespace Mithril.Shared.Reference;

/// <summary>
/// Parsed projection of a <c>BestowRecipeIfNotKnown(recipeInternalName)</c> entry in
/// <see cref="RecipeEntry.ResultEffects"/>. The target recipe resolves to a
/// <see cref="RecipeEntry"/> via <c>RecipesByInternalName</c>; this record snapshots the
/// fields the "Teaches" section needs so it can render without a second lookup.
/// </summary>
public sealed record TaughtRecipePreview(
    string RecipeInternalName,
    string DisplayName,
    string Skill,
    int SkillLevelReq)
{
    public string DisplayLine => SkillLevelReq > 0
        ? $"{DisplayName} ({Skill} {SkillLevelReq})"
        : $"{DisplayName} ({Skill})";
}
