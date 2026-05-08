namespace Elrond.ViewModels;

/// <summary>
/// One node in the Elrond skill picker tree. Leaves carry the active character's
/// level/XP for the skill; headers are non-selectable parents that exist purely
/// to group their children (e.g. <c>Augmentation</c> grouping its four
/// <c>*AugmentBrewing</c> children).
/// </summary>
public sealed record SkillNode(
    string Key,
    string DisplayName,
    int? CurrentLevel,
    long? CurrentXp,
    long? XpNeededForNextLevel,
    bool IsHeaderOnly,
    IReadOnlyList<SkillNode> Children)
{
    public bool IsSelectable => !IsHeaderOnly;
}
