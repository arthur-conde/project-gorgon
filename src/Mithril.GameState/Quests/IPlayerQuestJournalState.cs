namespace Mithril.GameState.Quests;

/// <summary>
/// Base type for a live quest-journal transition. <c>InternalName</c> is the
/// stable identifier used by <c>IReferenceDataService.QuestsByInternalName</c>;
/// <c>Timestamp</c> is the source log line's timestamp (UTC), not wall-clock —
/// consumers anchoring cooldowns need the in-game timeline.
///
/// <para>Past-tense participle subtypes per the #657 naming convention: each
/// concrete event carries an explicit <c>Player</c> prefix because the journal
/// folder lives on <c>IPlayerWorld</c>. Bus consumers pattern-match on subtype
/// rather than a <c>Kind</c> discriminator.</para>
/// </summary>
public abstract record PlayerQuestEvent(string InternalName, DateTime Timestamp);

/// <summary>Quest entered the player's active journal.</summary>
public sealed record PlayerQuestAccepted(string InternalName, DateTime Timestamp)
    : PlayerQuestEvent(InternalName, Timestamp);

/// <summary>
/// Quest left the player's active journal without being completed (player
/// abandoned it, or the bulk <c>ProcessLoadQuests</c> replace dropped it).
/// Distinct from <see cref="PlayerQuestCompleted"/> because no cooldown anchor
/// applies.
/// </summary>
public sealed record PlayerQuestAbandoned(string InternalName, DateTime Timestamp)
    : PlayerQuestEvent(InternalName, Timestamp);

/// <summary>
/// Quest was turned in. Removes from active set; downstream cooldown clocks
/// anchor on <see cref="PlayerQuestEvent.Timestamp"/>. No completion history is
/// retained inside the journal — module-side ledgers (e.g. Gandalf's
/// <c>DerivedTimerProgressService</c>) own cross-session continuity by
/// subscribing and persisting their own anchors.
/// </summary>
public sealed record PlayerQuestCompleted(string InternalName, DateTime Timestamp)
    : PlayerQuestEvent(InternalName, Timestamp);

/// <summary>An entry in the player's active quest journal.</summary>
public sealed record QuestJournalEntry(string InternalName, DateTimeOffset AcceptedAt);

/// <summary>
/// Canonical "quests in journal" map for the active character, derived by
/// folding the Player.log quest events (<c>ProcessLoadQuests</c> /
/// <c>ProcessBook("New Quest:" …)</c> / <c>ProcessCompleteQuest</c>) from
/// <see cref="Mithril.Shared.Logging.IPlayerLogStream"/>.
///
/// <para>State half of the (state, reference) split surfaced by world-sim
/// migration item #6 (see <c>docs/world-simulator.md</c>): this folder owns
/// the live per-character active set; quest <em>reference</em> data
/// (definitions, names, reuse times, requirements) continues to live in
/// <c>IReferenceDataService.Quests</c>. Quest-aware modules join the two
/// surfaces explicitly; the folder no longer conflates them.</para>
///
/// <para><b>No persistence.</b> Principle 13 of <c>docs/world-simulator.md</c>:
/// the world is a deterministic fold over the log stream; if the stream
/// doesn't carry it, the world doesn't either. <c>ProcessLoadQuests</c>
/// re-fires on every login / zone transition, so the active set rebuilds from
/// the next session's replay without any on-disk cache. Modules that need
/// cross-session continuity (e.g. Gandalf's repeatable-quest cooldown
/// anchors) maintain their own per-character ledgers populated by
/// subscribing to the events here — they are NOT served by a service-side
/// completion-history map. See #718.</para>
///
/// <para>Owning the journal centrally — rather than letting each quest-aware
/// module re-parse the log — avoids fan-out of the same journal state and
/// lets downstream consumers (<c>QuestSource</c> for repeatable cooldowns,
/// future Smaug repeatable-quest tracking, future quest planners) read a
/// single snapshot. Modules either query the live map via
/// <see cref="ActiveQuests"/> / <see cref="TryGetActive"/>, or
/// <see cref="Subscribe"/> for an atomic replay-then-live event stream.</para>
/// </summary>
public interface IPlayerQuestJournalState
{
    /// <summary>
    /// Snapshot of quests currently in the active character's journal, keyed
    /// by InternalName. Returns a copy — safe to enumerate without locking.
    /// </summary>
    IReadOnlyDictionary<string, QuestJournalEntry> ActiveQuests { get; }

    bool TryGetActive(string internalName, out QuestJournalEntry entry);

    /// <summary>
    /// Attach a handler that receives a synthesised
    /// <see cref="PlayerQuestAccepted"/> for every entry currently in
    /// <see cref="ActiveQuests"/>, then every live add/abandon/complete. Replay
    /// and live-attach are atomic — no event is lost, duplicated, or reordered
    /// relative to the canonical state.
    ///
    /// <para>Note: there is <b>no</b> on-subscribe replay of
    /// <see cref="PlayerQuestCompleted"/> events. The folder no longer owns
    /// cross-session completion history (see #718 / principle 13). Consumers
    /// that need historical completion anchors maintain their own per-character
    /// ledgers and migrate them from <c>quests.json</c> on startup if needed
    /// (Gandalf's <c>QuestCompletionImportService</c> does this for the
    /// pre-#718 persistence shape).</para>
    ///
    /// <para><b>Idempotency contract for <see cref="PlayerQuestCompleted"/>
    /// subscribers.</b> Same-session intra-Mithril duplicates are suppressed by
    /// the implementation's in-memory <c>_completedThisSession</c> map
    /// (replayed lines with a matching <c>(InternalName, Timestamp)</c> pair do
    /// not re-fire). However that map resets on every Mithril restart, so a
    /// <see cref="PlayerQuestCompleted"/> for the same
    /// <c>(InternalName, Timestamp)</c> pair MAY fire more than once across a
    /// Mithril restart that lands within a single PG session — each fresh
    /// attach replays the current PG session's <c>ProcessCompleteQuest</c>
    /// lines from scratch. Subscribers MUST therefore be past-anchored /
    /// idempotent on <see cref="PlayerQuestEvent.Timestamp"/>; a fresh
    /// observation cannot be inferred from "I got this event". Today
    /// <c>QuestSource.AnchorCompletionCooldown</c> meets this contract by
    /// stamping <c>DerivedTimerProgress.StartedAt</c> on the carried log
    /// timestamp — an overwrite-with-same-value is a no-op. Side-effect-emitting
    /// subscribers (toasts, audio alarms, "you just completed quest X" UI)
    /// should additionally gate on <c>Mode == Live</c> once worlds land per
    /// <c>docs/world-simulator.md</c>, and may want a per-session dedupe on the
    /// receiving side. See #718, #736.</para>
    ///
    /// <para>The handler is invoked synchronously under an internal lock both
    /// during replay (on the subscribing thread) and during live dispatch (on
    /// the ingestion-loop thread). Subscribers that do non-trivial work should
    /// dispatch off-thread immediately to avoid blocking ingestion.</para>
    ///
    /// <para>Dispose the returned subscription to stop receiving further
    /// events.</para>
    /// </summary>
    IDisposable Subscribe(Action<PlayerQuestEvent> handler);
}
