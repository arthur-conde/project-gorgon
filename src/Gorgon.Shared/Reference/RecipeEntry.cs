namespace Gorgon.Shared.Reference;

/// <summary>
/// Slim projection of one entry in recipes.json. Covers skill-leveling analysis:
/// which skill earns XP, how much, and whether first-time bonuses apply.
/// </summary>
public sealed record RecipeEntry(
    string Key,
    string Name,
    string InternalName,
    int IconId,
    string Skill,
    int SkillLevelReq,
    string RewardSkill,
    int RewardSkillXp,
    int RewardSkillXpFirstTime,
    int? RewardSkillXpDropOffLevel,
    float? RewardSkillXpDropOffPct,
    int? RewardSkillXpDropOffRate,
    IReadOnlyList<RecipeItemRef> Ingredients,
    IReadOnlyList<RecipeItemRef> ResultItems,
    string? PrereqRecipe = null,
    IReadOnlyList<RecipeItemRef>? ProtoResultItems = null);
