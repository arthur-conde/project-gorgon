namespace Mithril.GameState.Skills;

/// <summary>How a <see cref="SkillChange"/> was produced.</summary>
public enum SkillChangeKind
{
    /// <summary>
    /// From a <c>ProcessUpdateSkill</c> delta — a live, in-session progression
    /// tick. Carries a meaningful <see cref="SkillChange.XpGained"/>.
    /// </summary>
    Delta = 0,

    /// <summary>
    /// From a <c>ProcessLoadSkills</c> full snapshot — emitted only for skills
    /// whose projection actually differs from (or is new vs.) the prior state.
    /// A snapshot is not a gain event, so <see cref="SkillChange.XpGained"/> is
    /// <c>0</c> here even though the level/xp may have moved (e.g. progression
    /// that happened while Mithril wasn't tailing, or the periodic re-sync).
    /// </summary>
    SnapshotReplace = 1,
}

/// <summary>
/// A single skill's progression changed. The granular companion to
/// <see cref="IPlayerSkillState.Subscribe"/>'s whole-snapshot push: a consumer
/// that cares <em>which</em> skill moved (level-ups, XP feeds) subscribes via
/// <see cref="IPlayerSkillState.SubscribeChanges"/> instead of diffing
/// snapshots itself.
///
/// <list type="bullet">
///   <item><b>Level-up</b> is <c>Previous?.Level &lt; Current.Level</c>.</item>
///   <item><b>Just hit cap</b> is
///   <c>Previous is { IsCapped: false } &amp;&amp; Current.IsCapped</c> — and
///   note PG then goes silent for that skill (no further
///   <c>ProcessUpdateSkill</c>), so this is the last <see cref="Delta"/> you
///   will see for it until a <see cref="SkillChangeKind.SnapshotReplace"/>.</item>
/// </list>
/// </summary>
/// <param name="SkillKey">Skill internal name (the map key — e.g.
/// <c>"Anatomy_Bears"</c>). UI resolves to a display name; the model keeps the
/// key.</param>
/// <param name="Previous">The skill's projection before this change, or
/// <c>null</c> if it was previously unknown (first observation, or first time
/// seen after a cold start).</param>
/// <param name="Current">The skill's projection after this change.</param>
/// <param name="XpGained">XP earned on this tick for a
/// <see cref="SkillChangeKind.Delta"/> (chat-corroborated within a level; see
/// <see cref="Parsing.SkillProgressUpdateEvent"/>). Always <c>0</c> for a
/// <see cref="SkillChangeKind.SnapshotReplace"/>.</param>
/// <param name="Kind">Whether this came from a live delta or a snapshot
/// reconcile.</param>
/// <param name="Timestamp">UTC timestamp of the source log line.</param>
public readonly record struct SkillChange(
    string SkillKey,
    SkillProgressSnapshot? Previous,
    SkillProgressSnapshot Current,
    long XpGained,
    SkillChangeKind Kind,
    DateTime Timestamp);
