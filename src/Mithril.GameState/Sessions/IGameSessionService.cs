namespace Mithril.GameState.Sessions;

/// <summary>
/// Live PG session identity. Tails <see cref="Mithril.Shared.Logging.IPlayerLogStream"/>
/// for the <c>Logged in as character X. Time UTC=… Timezone Offset …</c> banner,
/// publishes the current <see cref="GameSession"/>, and re-anchors on every
/// fresh login. Sibling of <see cref="Mithril.GameState.Inventory.IInventoryService"/>
/// and <see cref="Mithril.GameState.Quests.IPlayerQuestJournalService"/>: same
/// atomic-replay Subscribe pattern so late joiners observe the current session
/// synchronously.
///
/// Also surfaces as <see cref="Mithril.Shared.Logging.ISessionAnchor"/> so
/// <see cref="Mithril.Shared.Logging.PlayerLogClock"/> (the Player.log L0
/// source clock, #513) can anchor log-line dates on the banner's UTC
/// instead of file mtime.
/// </summary>
public interface IGameSessionService
{
    /// <summary>
    /// Current session, or <c>null</c> if no banner has been parsed yet
    /// (e.g. Mithril attached before any login this run).
    /// </summary>
    GameSession? Current { get; }

    /// <summary>
    /// Raised when a new session is observed — first-time observation or PG
    /// re-login during the same Mithril run. Replaying the same banner does
    /// not re-fire (the service compares <see cref="GameSession.SessionId"/>
    /// before raising).
    /// </summary>
    event EventHandler<GameSession>? SessionStarted;

    /// <summary>
    /// Attach a handler that synchronously receives the current session (if
    /// any) followed by every subsequent <see cref="SessionStarted"/>. Replay
    /// and live delivery are atomic under an internal lock — no session is
    /// lost, duplicated, or reordered relative to the canonical
    /// <see cref="Current"/> view.
    ///
    /// Dispose the returned subscription to stop receiving further events.
    /// </summary>
    IDisposable Subscribe(Action<GameSession> handler);
}
