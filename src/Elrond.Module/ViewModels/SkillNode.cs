namespace Elrond.ViewModels;

/// <summary>
/// One row in the Elrond skill picker — a craftable cookbook section the active
/// character has the host skill for, with their current level/XP for that skill.
/// The picker is flat: hierarchy from <c>skills.json</c> Parents isn't surfaced
/// because the picker organises by recipe filing (<c>SortSkill</c>), which is
/// flat in the in-game cookbook.
/// </summary>
public sealed record SkillNode(
    string Key,
    string DisplayName,
    int CurrentLevel,
    long CurrentXp,
    long XpNeededForNextLevel);
