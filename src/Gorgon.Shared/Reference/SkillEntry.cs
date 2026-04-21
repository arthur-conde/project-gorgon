namespace Gorgon.Shared.Reference;

/// <summary>
/// Slim projection of one entry in skills.json. Links skill name to its XP table
/// and combat/noncombat classification.
/// </summary>
public sealed record SkillEntry(
    string Name,
    int Id,
    bool Combat,
    string XpTable,
    int MaxBonusLevels);
