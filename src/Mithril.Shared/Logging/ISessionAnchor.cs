namespace Mithril.Shared.Logging;

/// <summary>
/// Provides an authoritative UTC datetime anchor for the current PG session,
/// parsed from the in-file <c>Logged in as character … Time UTC=… Timezone Offset …</c>
/// banner. Consumed by <see cref="LogLineTimestampSequencer"/> to anchor the
/// date for <c>[HH:MM:SS]</c> prefixes; this beats anchoring on file mtime
/// (which drifts on copies, runs across midnight, and depends on system clock
/// state).
///
/// Lives in <c>Mithril.Shared</c> rather than <c>Mithril.GameState</c> so the
/// sequencer can depend on it without inverting the project graph. The
/// implementation (a <c>GameSessionService</c> sibling of
/// <c>IInventoryService</c> / <c>IQuestService</c>) ships in
/// <c>Mithril.GameState.Sessions</c> and is registered as both this interface
/// and the richer <c>IGameSessionService</c>.
/// </summary>
public interface ISessionAnchor
{
    /// <summary>
    /// UTC instant of the most recent <c>Logged in as character</c> banner
    /// observed in the live log, or <c>null</c> if no banner has been seen yet.
    /// Once non-null, only re-assigned on a fresh PG login (second banner) —
    /// not cleared on logout.
    /// </summary>
    DateTime? LoggedInUtc { get; }

    /// <summary>
    /// Raised when <see cref="LoggedInUtc"/> changes (first-time set, or
    /// PG re-login during the same Mithril run). Subscribers re-anchor any
    /// date-dependent state.
    /// </summary>
    event EventHandler? AnchorChanged;
}
