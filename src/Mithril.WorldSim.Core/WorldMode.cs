namespace Mithril.WorldSim;

/// <summary>
/// Whether a world is draining recorded frames toward the live tail
/// (<see cref="Replaying"/>) or has caught up and is blocking on the live
/// source-stream tail (<see cref="Live"/>). State derivation is mode-agnostic
/// (folders, composers, and views update internal state identically in both
/// modes); side-effect-emitting consumers (audio alarms, window flash, OS
/// notifications) gate on <c>Mode == Live</c> to avoid blasting the user with
/// replays of yesterday's alarms when Mithril restarts (principle 12).
/// </summary>
public enum WorldMode
{
    /// <summary>
    /// The world is draining recorded backlog frames from its producers
    /// toward the live tail. State updates apply; user-facing side effects
    /// do not fire.
    /// </summary>
    Replaying,

    /// <summary>
    /// The world has caught up to its source-stream tail and is now blocking
    /// on the next live append. State updates apply; user-facing side effects
    /// fire.
    /// </summary>
    Live,
}
