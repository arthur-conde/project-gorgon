namespace Mithril.GameState.Celestial;

/// <summary>
/// The local player's last-known lunar phase, plus the instant it was
/// measured. PG emits <c>ProcessSetCelestialInfo</c> on login and on every
/// phase roll-over, so this is far less stale than position — but it can
/// still lag if a phase boundary passed while the log wasn't being tailed.
/// <see cref="MeasuredAt"/> is the log line's UTC instant (Player.log
/// <c>[HH:MM:SS]</c> prefixes are UTC), exposed as a
/// <see cref="DateTimeOffset"/>. <see cref="RawPhase"/> is the verbatim log
/// token; <see cref="Phase"/> is its canonical mapping
/// (<see cref="MoonPhase.Unknown"/> ⇒ the token was unrecognised but
/// <see cref="RawPhase"/> is still authoritative).
/// </summary>
public sealed record CelestialInfo(MoonPhase Phase, string RawPhase, DateTimeOffset MeasuredAt)
{
    /// <summary>Human phrase — fixed name for a recognised phase, otherwise a
    /// CamelCase-split of the raw token.</summary>
    public string DisplayName => Phase.DisplayName(RawPhase);
}

/// <summary>
/// Shared live game-state: the player's last-known lunar phase. Mirrors
/// <see cref="Mithril.GameState.Movement.IPlayerPositionTracker"/> —
/// <see cref="Current"/> plus a replay-on-<see cref="Subscribe"/> handler so
/// late subscribers see the same view already-attached ones do.
/// </summary>
[Obsolete("Use Arda.World.Player.ICelestialState + IDomainEventSubscriber.Subscribe<CelestialInfoChanged> instead.")]
public interface IPlayerCelestialState
{
    /// <summary>
    /// Last lunar phase observed, or <c>null</c> before the first
    /// <c>ProcessSetCelestialInfo</c> line of the session is seen.
    /// </summary>
    CelestialInfo? Current { get; }

    /// <summary>
    /// Register a handler. If a phase is already known it is replayed
    /// synchronously before the call returns; subsequent phases are delivered
    /// live until the returned token is disposed.
    /// </summary>
    IDisposable Subscribe(Action<CelestialInfo> handler);
}
