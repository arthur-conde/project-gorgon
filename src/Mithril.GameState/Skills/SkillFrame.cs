using Mithril.GameState.Skills.Parsing;

namespace Mithril.GameState.Skills;

/// <summary>
/// World-simulator frame payload for the Player.log skill folder
/// (<see cref="PlayerSkillStateService"/>). The folder is registered with
/// <c>IPlayerWorld</c> for this payload type (issue #618 — Phase 1 of the
/// world-sim migration, validates the foundation end-to-end before the
/// bigger Phase 2 splits proceed). A skill-aware producer
/// (<see cref="Producers.SkillFrameProducer"/>) reads classified
/// LocalPlayer log envelopes, parses them via <see cref="SkillLogParser"/>,
/// and emits one of the two subtypes below for every skill-relevant line.
///
/// <para>The closed hierarchy keeps the folder's switch exhaustive at
/// compile time and lets a consumer pattern-match without an open-world
/// downcast.</para>
/// </summary>
public abstract record SkillFrame
{
    // Closed hierarchy — only the nested subtypes can extend this record.
    // (Using a non-public ctor would also forbid external derivation, but
    // the doc note on the abstract record carries the intent.)
    private protected SkillFrame() { }
}

/// <summary>
/// Frame payload representing a full <c>ProcessLoadSkills</c> snapshot. PG
/// emits this at login and on every zone / session transition; the folder
/// treats it as a wholesale replace, never a merge — same semantics as the
/// pre-migration <see cref="PlayerSkillStateService"/> (see the type docs
/// there).
/// </summary>
/// <param name="Skills">Every parsed skill row, in log order. Carries the
/// raw <see cref="SkillProgressRecord"/> tuples; the folder's projection
/// applies the caveat interpretation (capped / trainable / reference
/// enrichment).</param>
public sealed record SkillsSnapshotFrame(IReadOnlyList<SkillProgressRecord> Skills) : SkillFrame;

/// <summary>
/// Frame payload representing a single <c>ProcessUpdateSkill</c> delta.
/// Carries the absolute post-tick state of one skill plus the gross
/// <see cref="XpGained"/> on this tick (chat-corroborated within a level —
/// see <see cref="SkillProgressUpdateEvent"/>).
/// </summary>
/// <param name="Skill">The single skill record from the line's struct.</param>
/// <param name="XpGained">XP earned on this tick (the line's third
/// positional). Always <c>&gt;= 0</c>; <c>0</c> when the parser couldn't
/// parse the tail (treated as a no-gain delta — the struct remains
/// authoritative for state).</param>
public sealed record SkillProgressUpdateFrame(SkillProgressRecord Skill, long XpGained) : SkillFrame;
