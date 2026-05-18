using Mithril.Shared.Logging;

namespace Mithril.GameState.Skills.Parsing;

/// <summary>
/// One skill's progression facet exactly as Project Gorgon emits it inside the
/// <c>{type=…,raw=…,bonus=…,xp=…,tnl=…,max=…}</c> struct shared by the
/// <c>ProcessLoadSkills</c> (login / zone snapshot) and <c>ProcessUpdateSkill</c>
/// (per-skill delta) log lines.
///
/// <para>This is the raw parse record — a faithful 1:1 of the log fields, before
/// any caveat interpretation. <see cref="Mithril.GameState.Skills.SkillProgressSnapshot"/>
/// is the consumer-facing projection that layers the capped / trainable rules on
/// top.</para>
///
/// <list type="bullet">
///   <item><see cref="SkillKey"/> — the skill's internal name (the struct's
///   <c>type=</c>), e.g. <c>"Vampirism"</c>, <c>"Anatomy_Bears"</c>,
///   <c>"Performance_Dance"</c>. Carries the underscore form verbatim; UI is
///   responsible for resolving it to a display name (model keeps the key — the
///   project-wide skill-key→display-name convention).</item>
///   <item><see cref="Level"/> — the struct's <c>raw=</c>: the unbuffed base
///   level. This is the progression-truth value; never sum it with
///   <see cref="BonusLevels"/>.</item>
///   <item><see cref="BonusLevels"/> — the struct's <c>bonus=</c>: levels added
///   by gear / buffs / forms. Volatile (it moves as the player swaps equipment
///   or shifts form — cf. the <c>ProcessSetActiveSkills</c> mount cycling) and is
///   <em>not</em> progression.</item>
///   <item><see cref="XpTowardNextLevel"/> — the struct's <c>xp=</c>: XP
///   accumulated into the current level. Meaningless once
///   <see cref="MaxLevel"/> is reached (PG leaves a stale value there).</item>
///   <item><see cref="XpNeededForNextLevel"/> — the struct's <c>tnl=</c>
///   ("to next level"): the threshold for the next level. Also stale at the
///   cap.</item>
///   <item><see cref="MaxLevel"/> — the struct's <c>max=</c>: the level cap.
///   <c>0</c> marks a non-trainable pseudo-skill (Augmentation / Performance /
///   Phrenology and similar, which report <c>raw=0,bonus=N,max=0</c>).</item>
/// </list>
/// </summary>
public readonly record struct SkillProgressRecord(
    string SkillKey,
    int Level,
    int BonusLevels,
    long XpTowardNextLevel,
    long XpNeededForNextLevel,
    int MaxLevel);

/// <summary>
/// A full skill-table snapshot, parsed from a <c>ProcessLoadSkills(…)</c> line.
/// Project Gorgon emits this right after <c>Logged in as character &lt;name&gt;</c>
/// and again on zone / session transitions, so it is <b>authoritative and
/// complete</b> — <see cref="Mithril.GameState.Skills.PlayerSkillStateService"/>
/// treats it as a wholesale replace of the tracked state, never a merge. The
/// re-fire on zone changes is what lets the tracker self-heal after a
/// mid-session start (see the service docs).
/// </summary>
/// <param name="Timestamp">The source log line's timestamp (UTC — Player.log's
/// <c>[HH:MM:SS]</c> prefix is UTC).</param>
/// <param name="Skills">Every skill in the dump, in log order. ~125 entries in
/// practice, including capped (<c>raw==max</c>) and pseudo (<c>max==0</c>)
/// rows — the parser does not filter; interpretation is the snapshot layer's
/// job.</param>
public sealed record SkillsSnapshotEvent(DateTime Timestamp, IReadOnlyList<SkillProgressRecord> Skills)
    : LogEvent(Timestamp);

/// <summary>
/// A single skill's progression changed, parsed from a
/// <c>ProcessUpdateSkill({…}, &lt;bool&gt;, &lt;delta&gt;, 0, 0)</c> line. Only
/// the leading struct is consumed: it carries the new authoritative state, so
/// the tracker upserts from it directly. The trailing positionals (announce
/// bool, XP delta, two zeros) are intentionally <b>not</b> parsed — their
/// semantics are only inferred from a small sample (verification owed, see the
/// service docs / issue #462) and the struct alone is sufficient for state.
/// </summary>
/// <param name="Timestamp">The source log line's timestamp (UTC).</param>
/// <param name="Skill">The single skill record from the line's struct.</param>
public sealed record SkillProgressUpdateEvent(DateTime Timestamp, SkillProgressRecord Skill)
    : LogEvent(Timestamp);
