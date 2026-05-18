namespace Mithril.GameState.Movement;

/// <summary>
/// The local player's last-known world position, plus the instant it was
/// measured. Sparse by nature — PG only logs <c>ProcessNewPosition</c> on
/// teleport / zone-in / certain combat blinks, so <see cref="MeasuredAt"/>
/// can lag wall-clock by minutes-to-hours while the player walks around.
/// <see cref="MeasuredAt"/> is the log line's UTC instant (Player.log
/// <c>[HH:MM:SS]</c> prefixes are UTC), exposed as a <see cref="DateTimeOffset"/>.
/// </summary>
public sealed record PlayerPosition(double X, double Y, double Z, DateTimeOffset MeasuredAt);

/// <summary>
/// Shared live game-state: the player's last-known position. Mirrors the
/// <see cref="Mithril.GameState.Sessions.IGameSessionService"/> shape —
/// <see cref="Current"/> plus a replay-on-<see cref="Subscribe"/> handler so
/// late subscribers see the same view already-attached ones do.
/// </summary>
public interface IPlayerPositionTracker
{
    /// <summary>
    /// Last position observed, or <c>null</c> before the first
    /// <c>ProcessNewPosition</c> line of the session is seen.
    /// </summary>
    PlayerPosition? Current { get; }

    /// <summary>
    /// Register a handler. If a position is already known it is replayed
    /// synchronously before the call returns; subsequent positions are
    /// delivered live until the returned token is disposed.
    /// </summary>
    IDisposable Subscribe(Action<PlayerPosition> handler);
}
