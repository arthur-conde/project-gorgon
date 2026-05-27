namespace Mithril.GameReports;

/// <summary>
/// Parsed snapshot of a character exported via the game's /exportcharacter command.
/// </summary>
public sealed record CharacterSnapshot(
    string Name,
    string Server,
    DateTimeOffset ExportedAt,
    IReadOnlyDictionary<string, CharacterSkill> Skills,
    IReadOnlyDictionary<string, int> RecipeCompletions,
    IReadOnlyDictionary<string, string> NpcFavor);

/// <summary>
/// One skill's progress from a character export.
/// </summary>
public sealed record CharacterSkill(
    int Level,
    int BonusLevels,
    long XpTowardNextLevel,
    long XpNeededForNextLevel)
{
    /// <summary>
    /// Level shown in-game and used for recipe gates (<c>raw + bonus</c>).
    /// <see cref="Level"/> alone is the progression track; <see cref="BonusLevels"/>
    /// is gear/buff-derived and volatile.
    /// </summary>
    public int EffectiveLevel => Level + BonusLevels;
}
