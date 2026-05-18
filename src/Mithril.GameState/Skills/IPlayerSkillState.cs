using Mithril.GameState.Skills.Parsing;

namespace Mithril.GameState.Skills;

/// <summary>
/// Where the current <see cref="PlayerSkillSnapshot"/> came from. Lets a
/// consumer prefer live data and surface provenance / freshness.
/// </summary>
public enum SkillStateSource
{
    /// <summary>
    /// Nothing observed yet — the tracker has not seen a <c>ProcessLoadSkills</c>
    /// or <c>ProcessUpdateSkill</c> line this session. The snapshot is
    /// <see cref="PlayerSkillSnapshot.Empty"/>.
    /// </summary>
    None = 0,

    /// <summary>
    /// Built by tailing <c>Player.log</c> (<c>ProcessLoadSkills</c> /
    /// <c>ProcessUpdateSkill</c>). The default and, today, only populated
    /// source. The enum is left open so a future export-fed source can be
    /// distinguished without a breaking change.
    /// </summary>
    LiveLog = 1,
}

/// <summary>
/// Consumer-facing projection of one skill's progression — the raw
/// <see cref="SkillProgressRecord"/> with the data caveats from issue #462
/// interpreted into intent-revealing predicates so every consumer applies them
/// the same way.
/// </summary>
/// <param name="Level">Unbuffed base level (the log's <c>raw</c>). This is the
/// progression-truth value the leveling constraint set keys off — never add
/// <see cref="BonusLevels"/> to it for "level achieved".</param>
/// <param name="BonusLevels">Gear / buff / form level bonus (the log's
/// <c>bonus</c>). Volatile and <em>not</em> progression; surfaced so a consumer
/// can show effective level if it wants, kept strictly separate from
/// <see cref="Level"/>.</param>
/// <param name="XpTowardNextLevel">XP into the current level (the log's
/// <c>xp</c>). Only meaningful when <see cref="IsCapped"/> is false.</param>
/// <param name="XpNeededForNextLevel">XP threshold for the next level (the
/// log's <c>tnl</c>). PG leaves a stale value here at the cap — only meaningful
/// when <see cref="IsCapped"/> is false.</param>
/// <param name="MaxLevel">Level cap (the log's <c>max</c>). <c>0</c> for a
/// non-trainable pseudo-skill.</param>
public readonly record struct SkillProgressSnapshot(
    int Level,
    int BonusLevels,
    long XpTowardNextLevel,
    long XpNeededForNextLevel,
    int MaxLevel)
{
    /// <summary>
    /// True when the skill advances via an XP curve (<c>max &gt; 0</c>). False
    /// for <b>umbrella skills</b> — real skills flagged <c>IsUmbrellaSkill</c>
    /// /<c>XpTable:"None"</c> in <c>skills.json</c> (Augmentation / Performance /
    /// Phrenology, …) whose level is derived from member sub-skills and carried
    /// in <see cref="BonusLevels"/> (<see cref="Level"/> stays 0). They never
    /// gain XP, so they have no per-level curve and report <c>max=0</c>; that is
    /// the runtime proxy for the reference classification (this assembly takes
    /// no reference-data dependency by design). Kept in the snapshot — flagged,
    /// not dropped — so a consumer decides; the leveling constraint set should
    /// exclude them.
    /// </summary>
    public bool IsTrainable => MaxLevel > 0;

    /// <summary>
    /// True when the skill has reached its cap (<c>raw &gt;= max</c>, max &gt; 0).
    /// <see cref="XpTowardNextLevel"/> / <see cref="XpNeededForNextLevel"/> are
    /// stale and meaningless once capped — do not present "N xp to next level"
    /// for a capped skill.
    /// </summary>
    public bool IsCapped => MaxLevel > 0 && Level >= MaxLevel;
}

/// <summary>
/// An immutable, atomically-consistent view of the player's whole skill table
/// at one instant: the per-skill map plus when and from where it was measured.
/// Returned by value so a consumer never observes a torn read (map updated but
/// timestamp not, or vice versa).
/// </summary>
public sealed class PlayerSkillSnapshot
{
    /// <summary>The empty snapshot — no skills, never measured, source
    /// <see cref="SkillStateSource.None"/>. The cold-start value.</summary>
    public static PlayerSkillSnapshot Empty { get; } =
        new(new Dictionary<string, SkillProgressSnapshot>(StringComparer.Ordinal), null, SkillStateSource.None);

    internal PlayerSkillSnapshot(
        IReadOnlyDictionary<string, SkillProgressSnapshot> skills,
        DateTime? measuredAt,
        SkillStateSource source)
    {
        Skills = skills;
        MeasuredAt = measuredAt;
        Source = source;
    }

    /// <summary>
    /// Skill internal name → progression. Internal-name keyed (<c>"Vampirism"</c>,
    /// <c>"Anatomy_Bears"</c>) — UI resolves the key to a display name, the
    /// model keeps the key (project-wide convention). Ordinal-keyed.
    /// </summary>
    public IReadOnlyDictionary<string, SkillProgressSnapshot> Skills { get; }

    /// <summary>
    /// UTC timestamp of the log line that produced this snapshot, or
    /// <c>null</c> if nothing has been observed yet. (Player.log timestamps are
    /// UTC.) Surface this for freshness — a live snapshot can still be minutes
    /// old if the player has been idle in one zone.
    /// </summary>
    public DateTime? MeasuredAt { get; }

    /// <summary>Provenance of this snapshot.</summary>
    public SkillStateSource Source { get; }

    /// <summary>Convenience lookup for one skill.</summary>
    public bool TryGet(string skillKey, out SkillProgressSnapshot progress)
        => Skills.TryGetValue(skillKey, out progress);
}

/// <summary>
/// Shared, <c>Player.log</c>-fed live view of the player's current skill
/// progression — every skill, without requiring a character re-export.
///
/// <para>Built by tailing two log lines (<c>ProcessLoadSkills</c> = full
/// snapshot at login / zone change; <c>ProcessUpdateSkill</c> = per-skill
/// delta). Because <c>ProcessLoadSkills</c> re-fires on zone transitions, the
/// tracker <b>self-heals</b>: even if Mithril starts tailing mid-session, the
/// next zone change re-establishes the full table. Until the first
/// <c>ProcessLoadSkills</c> of the session is observed, <see cref="Current"/>
/// is <see cref="PlayerSkillSnapshot.Empty"/> (or partial, if only isolated
/// <c>ProcessUpdateSkill</c> lines have been seen) — this warm-up window is the
/// documented contract, not a bug.</para>
///
/// <para>This is neutral shared infrastructure (it does not know about leveling
/// math or any module). A consumer that needs the leveling-engine
/// <c>SkillState</c> shape adapts this projection itself — mirroring how
/// Elrond's <c>SnapshotPlanInput</c> already adapts the character export — so
/// <c>Mithril.GameState</c> takes no dependency on <c>Mithril.Leveling</c>.</para>
/// </summary>
public interface IPlayerSkillState
{
    /// <summary>
    /// The current immutable snapshot. Atomically consistent: the map,
    /// <see cref="PlayerSkillSnapshot.MeasuredAt"/>, and
    /// <see cref="PlayerSkillSnapshot.Source"/> always belong to the same
    /// observation. Never null — <see cref="PlayerSkillSnapshot.Empty"/> before
    /// the first observation.
    /// </summary>
    PlayerSkillSnapshot Current { get; }

    /// <summary>
    /// Attach a handler that is invoked immediately with the
    /// <see cref="Current"/> snapshot (replay) and then again on every
    /// subsequent change. Replay + live-attach are atomic under an internal
    /// lock, so a late subscriber cannot miss the snapshot that landed between
    /// resolving the service and subscribing.
    ///
    /// <para>The handler runs synchronously under the tracker's lock — both for
    /// the replay (on the caller's thread) and for live dispatch (on the log
    /// ingestion thread). Do non-trivial work off-thread. Dispose the returned
    /// subscription to stop receiving events.</para>
    /// </summary>
    IDisposable Subscribe(Action<PlayerSkillSnapshot> handler);

    /// <summary>
    /// Attach a handler that receives a granular <see cref="SkillChange"/> per
    /// skill that actually moved — the channel for consumers that care
    /// <em>which</em> skill changed (level-ups, XP feeds) rather than the whole
    /// snapshot.
    ///
    /// <para>Unlike <see cref="Subscribe"/> there is <b>no replay</b>: a
    /// <see cref="SkillChange"/> is an event, not state. A late subscriber sees
    /// changes from the moment it attaches; for current state it reads
    /// <see cref="Current"/>. A <c>ProcessLoadSkills</c> emits one
    /// <see cref="SkillChangeKind.SnapshotReplace"/> only for skills whose
    /// projection differs from (or is new vs.) the prior state — a no-op
    /// re-sync produces nothing. A <c>ProcessUpdateSkill</c> emits one
    /// <see cref="SkillChangeKind.Delta"/> carrying
    /// <see cref="SkillChange.XpGained"/>.</para>
    ///
    /// <para>Same threading contract as <see cref="Subscribe"/>: the handler
    /// runs synchronously under the tracker's lock on the ingestion thread —
    /// do non-trivial work off-thread. Dispose to stop receiving.</para>
    /// </summary>
    IDisposable SubscribeChanges(Action<SkillChange> handler);
}
