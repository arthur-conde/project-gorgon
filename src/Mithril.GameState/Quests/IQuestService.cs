namespace Mithril.GameState.Quests;

/// <summary>
/// A live quest-journal transition. <paramref name="InternalName"/> is the
/// stable identifier used by <c>IReferenceDataService.QuestsByInternalName</c>;
/// <paramref name="Timestamp"/> is the source log line's timestamp (UTC), not
/// wall-clock — consumers anchoring cooldowns need the in-game timeline.
/// </summary>
public readonly record struct QuestEvent(
    QuestEventKind Kind,
    string InternalName,
    DateTime Timestamp);

public enum QuestEventKind
{
    /// <summary>Quest entered the player's active journal.</summary>
    Accepted,
    /// <summary>Quest left the player's active journal without being completed
    /// (player abandoned it, or the bulk <c>ProcessLoadQuests</c> replace
    /// dropped it). Distinct from <see cref="Completed"/> because no cooldown
    /// anchor applies.</summary>
    Abandoned,
    /// <summary>Quest was turned in. Removes from active set and stamps
    /// <see cref="IQuestService.CompletionHistory"/>; downstream cooldown
    /// clocks anchor on <see cref="QuestEvent.Timestamp"/>.</summary>
    Completed,
}

/// <summary>An entry in the player's active quest journal.</summary>
public sealed record QuestJournalEntry(string InternalName, DateTimeOffset AcceptedAt);

/// <summary>The most recent completion observation for a single quest.</summary>
public sealed record QuestCompletionState(string InternalName, DateTimeOffset LastCompletedAt);

/// <summary>
/// Canonical "quests in journal" + "when did I last complete X?" map for the
/// active character, derived by tailing <c>ProcessLoadQuests</c> /
/// <c>ProcessBook("New Quest:" …)</c> / <c>ProcessCompleteQuest</c> on
/// <see cref="Mithril.Shared.Logging.IPlayerLogStream"/>. Persisted per-character
/// to <c>characters/{slug}/quests.json</c> so completion anchors survive
/// across sessions (a quest completed three days ago needs a remembered
/// timestamp; today's session log won't carry that <c>ProcessCompleteQuest</c>
/// line).
///
/// Owning this centrally — rather than letting each quest-aware module
/// re-parse the log — avoids fan-out of the same journal state and lets
/// downstream consumers (<c>QuestSource</c> for repeatable cooldowns, future
/// Smaug repeatable-quest tracking, future quest planners) read a single
/// snapshot. Modules either query the live maps via
/// <see cref="ActiveQuests"/> / <see cref="CompletionHistory"/> /
/// <see cref="TryGetActive"/> / <see cref="TryGetCompletion"/>, or
/// <see cref="Subscribe"/> for an atomic replay-then-live event stream.
/// </summary>
public interface IQuestService
{
    /// <summary>
    /// Snapshot of quests currently in the active character's journal, keyed
    /// by InternalName. Returns a copy — safe to enumerate without locking.
    /// </summary>
    IReadOnlyDictionary<string, QuestJournalEntry> ActiveQuests { get; }

    /// <summary>
    /// Snapshot of per-quest last-completion timestamps for the active
    /// character. Drives Gandalf's cooldown anchor and any "when did I last
    /// do X?" UX. Returns a copy — safe to enumerate without locking.
    /// </summary>
    IReadOnlyDictionary<string, QuestCompletionState> CompletionHistory { get; }

    bool TryGetActive(string internalName, out QuestJournalEntry entry);

    bool TryGetCompletion(string internalName, out QuestCompletionState state);

    /// <summary>
    /// Attach a handler that receives a synthesised
    /// <see cref="QuestEventKind.Accepted"/> for every entry currently in
    /// <see cref="ActiveQuests"/>, then a synthesised
    /// <see cref="QuestEventKind.Completed"/> for every entry in
    /// <see cref="CompletionHistory"/>, then every live add/abandon/complete.
    /// Replay and live-attach are atomic — no event is lost, duplicated, or
    /// reordered relative to the canonical state.
    ///
    /// The handler is invoked synchronously under an internal lock both during
    /// replay (on the subscribing thread) and during live dispatch (on the
    /// ingestion-loop thread). Subscribers that do non-trivial work should
    /// dispatch off-thread immediately to avoid blocking ingestion.
    ///
    /// On character switch the service atomically swaps to the new character's
    /// state and fires diff-events: <see cref="QuestEventKind.Abandoned"/> for
    /// quests in the old active set that aren't in the new,
    /// <see cref="QuestEventKind.Accepted"/> for the new entries, and
    /// <see cref="QuestEventKind.Completed"/> for every entry in the new
    /// completion history (subscribers maintaining mirrors should idempotently
    /// apply these).
    ///
    /// Dispose the returned subscription to stop receiving further events.
    /// </summary>
    IDisposable Subscribe(Action<QuestEvent> handler);
}
