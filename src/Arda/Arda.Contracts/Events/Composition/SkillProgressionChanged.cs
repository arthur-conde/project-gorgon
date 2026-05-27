using Arda.Abstractions.Logs;

namespace Arda.Composition.Events;

/// <summary>
/// Published by the <c>PlayerProgressionComposer</c> when a single skill gains XP
/// or levels up. Carries the enriched skill snapshot, the XP delta, and the
/// metadata of the triggering log line so consumers can correlate temporally
/// (consistent with every other composition event).
/// </summary>
public readonly record struct SkillProgressionChanged(
    string SkillKey,
    EnrichedSkill Skill,
    int XpGained,
    LogLineMetadata Metadata);
