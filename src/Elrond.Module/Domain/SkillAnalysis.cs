using Mithril.Shared.Reference;

namespace Elrond.Domain;

/// <summary>
/// Result of analyzing a single skill for a character: current XP state,
/// available recipes with their effective XP, and future level milestones.
/// When <see cref="GoalLevel"/> is set, <see cref="XpRemaining"/> and
/// <see cref="RecipeAnalysis.CompletionsToLevel"/> reflect the total gap to that goal.
/// <para/>
/// <see cref="IsUmbrellaSection"/> is true when the section's host skill has no
/// XpTable (e.g. Phrenology, Anatomy umbrella categories). The view degrades
/// the section header to <c>—</c> placeholders for level/XP/remaining since
/// those values are meaningless for skills that aren't directly levelable.
/// </summary>
public sealed record SkillAnalysis(
    string SkillName,
    int CurrentLevel,
    long CurrentXp,
    long XpNeededForNextLevel,
    long XpRemaining,
    IReadOnlyList<RecipeAnalysis> Recipes,
    IReadOnlyList<XpMilestone> Milestones,
    int? GoalLevel = null,
    bool IsUmbrellaSection = false);

/// <summary>
/// One recipe's analysis for a given cookbook section + character. <see cref="RewardSkill"/>
/// names the skill that actually earns XP when this recipe is crafted; it may differ from
/// the section the recipe is filed under (a Fish Stew filed in <c>Cooking</c> rewards
/// <c>Fishing</c> XP), and the row's <see cref="EffectiveXp"/>/<see cref="CompletionsToLevel"/>
/// reflect that reward skill, not the section.
/// <para/>
/// <see cref="RewardSkillCurrentLevel"/>/<see cref="RewardSkillCurrentXp"/>/<see cref="RewardSkillXpNeededForNextLevel"/>
/// denormalise the active character's progress in this recipe's reward skill so
/// the detail pane can show "where you stand on the skill this craft moves" without
/// re-querying the character. <see cref="RewardSkillDiffersFromSection"/> drives the
/// visibility of that target-skill panel — when it's false, the section header above
/// already covers it.
/// </summary>
public sealed record RecipeAnalysis(
    string RecipeKey,
    string RecipeName,
    string InternalName,
    int IconId,
    int LevelRequired,
    int BaseXp,
    int FirstTimeXp,
    int TimesCompleted,
    bool IsKnown,
    bool FirstTimeBonusAvailable,
    int EffectiveXp,
    int NextCraftXp,
    int? CompletionsToLevel,
    double Complexity,
    double? Efficiency,
    IReadOnlyList<RecipeIngredientDisplay> Ingredients,
    IReadOnlyList<CraftedGearPreview> CraftedOutputs,
    string RewardSkill = "",
    string RewardSkillDisplayName = "",
    int RewardSkillCurrentLevel = 0,
    long RewardSkillCurrentXp = 0,
    long RewardSkillXpNeededForNextLevel = 0,
    bool RewardSkillDiffersFromSection = false);

/// <summary>Display-ready ingredient for a recipe tooltip.</summary>
public sealed record RecipeIngredientDisplay(string Name, int IconId, int StackSize, float? ChanceToConsume);

/// <summary>
/// XP milestone for a future level.
/// </summary>
public sealed record XpMilestone(
    int Level,
    long XpRequired,
    long CumulativeXpFromCurrent);

/// <summary>
/// A single step in the simulation's optimal crafting plan.
/// </summary>
public sealed record SimulationStep(
    string RecipeKey,
    string RecipeName,
    int IconId,
    int Completions,
    int XpPerCompletion,
    bool UsesFirstTimeBonus,
    int FirstTimeBonusXp,
    long TotalXpFromStep,
    int LevelAtStart,
    int LevelAtEnd);

/// <summary>
/// Full result of the leveling simulation/optimization.
/// </summary>
public sealed record SimulationResult(
    string SkillName,
    int StartLevel,
    int GoalLevel,
    long TotalXpNeeded,
    int TotalCompletions,
    IReadOnlyList<SimulationStep> Steps);
