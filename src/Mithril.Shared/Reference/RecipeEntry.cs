namespace Mithril.Shared.Reference;

/// <summary>
/// Slim projection of one entry in recipes.json. Covers skill-leveling analysis:
/// which skill earns XP, how much, and whether first-time bonuses apply.
/// <para/>
/// Three skill fields, distinct concerns:
/// <list type="bullet">
///   <item><see cref="Skill"/> — the skill required to use the recipe (paired with <see cref="SkillLevelReq"/>).</item>
///   <item><see cref="RewardSkill"/> — the skill that earns XP when the recipe is crafted.</item>
///   <item><see cref="SortSkill"/> — where the recipe is filed in the in-game cookbook UI; may differ
///         from <see cref="RewardSkill"/> (e.g. fish-based food rewards Fishing XP but files under Cooking).
///         Null when the recipe files under <see cref="RewardSkill"/>.</item>
/// </list>
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
    IReadOnlyList<RecipeIngredient> Ingredients,
    IReadOnlyList<RecipeItemRef> ResultItems,
    string? PrereqRecipe = null,
    IReadOnlyList<RecipeItemRef>? ProtoResultItems = null,
    IReadOnlyList<string>? ResultEffects = null,
    string? SortSkill = null);
