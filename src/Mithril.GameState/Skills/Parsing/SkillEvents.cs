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
///   <c>0</c> marks an <b>umbrella skill</b> — a <em>real</em> skill flagged
///   <c>IsUmbrellaSkill</c> with <c>XpTable:"None"</c> in <c>skills.json</c>
///   (Augmentation / Performance / Phrenology, …). It never gains XP; its level
///   is derived from member sub-skills and carried in <see cref="BonusLevels"/>
///   (so it reports <c>raw=0,bonus=N,max=0</c>). <c>max==0</c> is a runtime
///   <em>proxy</em> for the authoritative <c>SkillEntry.XpTable == "None"</c>,
///   verified exact (skills.json is keyed by this same <c>type=</c> token). The
///   tracker stays log-only by deliberate choice — the parser/service remain
///   pure-string and unit-testable without a DI surface — even though
///   <c>IReferenceDataService.Skills</c> is already available (this assembly
///   already injects <c>IReferenceDataService</c> in Inventory/Quests).
///   Optional authoritative enrichment is the independent follow-up #470.</item>
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
/// <c>ProcessUpdateSkill({…}, &lt;bool&gt;, &lt;gained&gt;, 0, 0)</c> line. The
/// leading struct carries the new authoritative absolute state; the third
/// positional (<c>&lt;gained&gt;</c>) is the XP earned on this tick.
///
/// <para><b>Why we trust <see cref="XpGained"/>.</b> It was triangulated
/// against the authoritative chat <c>[Status] You earned N XP in &lt;Skill&gt;</c>
/// line across the Player.log(UTC)/ChatLogs(local) offset — Endurance 26,
/// Psychology 577, Anatomy_Bears 48 all matched exactly. A live level-up
/// capture then confirmed it holds <em>across the rollover</em>: Tailoring
/// <c>raw=9,xp=199,tnl=210, …160</c> then <c>raw=10,xp=149,tnl=420, …160</c>,
/// with chat "earned 160 XP and reached level 12 in Tailoring" — i.e.
/// <see cref="XpGained"/> is the <b>gross</b> XP gained that tick (matching
/// chat), the engine does <em>not</em> split it pre/post-level, so a consumer
/// summing it stays correct through level-ups. The trailing
/// <c>0, 0</c> positionals were observed to stay <c>0, 0</c> <em>through</em> a
/// level-up — they are <b>not</b> a levels-gained / skill-up count; treat as
/// reserved. The announce bool's batch-vs-discrete meaning is informational
/// only and still not parsed. A level-up is conveyed solely by <c>raw</c>
/// incrementing on the next event (PG emits no dedicated level-up line);
/// the chat "reached level N" uses the <em>effective</em> level
/// (<c>raw + bonus</c>), whereas <c>raw</c> here is the base.</para>
/// </summary>
/// <param name="Timestamp">The source log line's timestamp (UTC).</param>
/// <param name="Skill">The single skill record from the line's struct.</param>
/// <param name="XpGained">The third positional — XP earned this tick,
/// chat-corroborated within a level.</param>
public sealed record SkillProgressUpdateEvent(DateTime Timestamp, SkillProgressRecord Skill, long XpGained)
    : LogEvent(Timestamp);
