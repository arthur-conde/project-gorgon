namespace Gorgon.Shared.Character;

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
    long XpNeededForNextLevel);
