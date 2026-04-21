namespace Elrond.Domain;

/// <summary>
/// Result of analyzing a single skill for a character: current XP state,
/// available recipes with their effective XP, and future level milestones.
/// When <see cref="GoalLevel"/> is set, <see cref="XpRemaining"/> and
/// <see cref="RecipeAnalysis.CompletionsToLevel"/> reflect the total gap to that goal.
/// </summary>
public sealed record SkillAnalysis(
    string SkillName,
    int CurrentLevel,
    long CurrentXp,
    long XpNeededForNextLevel,
    long XpRemaining,
    IReadOnlyList<RecipeAnalysis> Recipes,
    IReadOnlyList<XpMilestone> Milestones,
    int? GoalLevel = null);

/// <summary>
/// One recipe's analysis for a given skill and character.
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
    int? CompletionsToLevel,
    IReadOnlyList<RecipeIngredientDisplay> Ingredients);

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
