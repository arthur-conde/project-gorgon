namespace Arda.Composition.Events;

/// <summary>
/// Published by the <c>PlayerProgressionComposer</c> when a single skill gains XP
/// or levels up. Carries the enriched skill snapshot and the XP delta.
/// </summary>
public readonly record struct SkillProgressionChanged(
    string SkillKey,
    EnrichedSkill Skill,
    int XpGained);
