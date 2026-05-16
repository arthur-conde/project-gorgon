namespace Mithril.Leveling;

/// <summary>
/// One skill's progression facet — the neutral equivalent of the export's per-skill
/// record, decoupled from <c>CharacterSnapshot</c> so callers can feed *hypothetical*
/// states (e.g. the #227 planner projecting a future level) rather than only real exports.
/// </summary>
public readonly record struct SkillProgress(
    int Level,
    int BonusLevels,
    long XpTowardNextLevel,
    long XpNeededForNextLevel);

/// <summary>
/// Multi-skill progression state. Recipes can grant XP in more than one skill at once
/// (and the planner exploits that), so this is keyed by skill rather than scoped to a
/// single target. Stateless in time — PG has no cooldowns / daily resets — so a
/// <see cref="SkillState"/> fully describes the leveling inputs at a point in the plan.
/// </summary>
public sealed class SkillState
{
    private readonly IReadOnlyDictionary<string, SkillProgress> _skills;

    public SkillState(IReadOnlyDictionary<string, SkillProgress> skills)
        => _skills = skills ?? throw new ArgumentNullException(nameof(skills));

    /// <summary>Empty state — every skill reads as level 0 / unknown.</summary>
    public static SkillState Empty { get; } =
        new(new Dictionary<string, SkillProgress>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, SkillProgress> Skills => _skills;

    public bool TryGet(string skill, out SkillProgress progress)
        => _skills.TryGetValue(skill, out progress);

    /// <summary>Current level in <paramref name="skill"/>, or 0 if the skill is absent.</summary>
    public int LevelOf(string skill)
        => _skills.TryGetValue(skill, out var p) ? p.Level : 0;

    public bool Knows(string skill) => _skills.ContainsKey(skill);
}

/// <summary>
/// Cumulative-in-completion recipe history (recipe <c>InternalName</c> → times completed).
/// Backs the first-time-per-character bonus: the math is time-stateless but the bonus is
/// a one-shot keyed on whether the recipe has ever been completed on this character.
/// </summary>
public sealed class RecipeHistory
{
    private readonly IReadOnlyDictionary<string, int> _completions;

    public RecipeHistory(IReadOnlyDictionary<string, int> completions)
        => _completions = completions ?? throw new ArgumentNullException(nameof(completions));

    /// <summary>Empty history — nothing has been crafted yet.</summary>
    public static RecipeHistory Empty { get; } =
        new(new Dictionary<string, int>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, int> Completions => _completions;

    /// <summary>
    /// True when the recipe is in the history at all. Mirrors Elrond's "known" convention:
    /// a recipe with a completion entry (even count 0) has been learned.
    /// </summary>
    public bool IsKnown(string recipeInternalName)
        => !string.IsNullOrEmpty(recipeInternalName) && _completions.ContainsKey(recipeInternalName);

    public int CompletionCount(string recipeInternalName)
        => !string.IsNullOrEmpty(recipeInternalName)
           && _completions.TryGetValue(recipeInternalName, out var c) ? c : 0;
}
