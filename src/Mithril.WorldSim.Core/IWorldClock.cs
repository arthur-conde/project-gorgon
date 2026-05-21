namespace Mithril.WorldSim;

/// <summary>
/// A world's simulated wall-clock. <see cref="Now"/> is always the timestamp of
/// the most recently applied frame — there is no live-mode interpolation, no
/// continuous-time abstraction (principle 13 — "calendar time is a domain event,
/// not a clock read"). Consumers that care about time progression subscribe to
/// <c>CalendarTimeAdvanced</c> domain events on the world's bus instead of polling
/// this clock. Real wall-clock (<see cref="TimeProvider.System"/>) is used only
/// inside the world's merger for blocking on the live source-stream tail; never
/// by folders, composers, views, or modules.
/// </summary>
public interface IWorldClock
{
    /// <summary>
    /// Simulated wall-clock = timestamp of the most recently applied frame.
    /// Weakly monotonic at 1-second resolution (PG's timestamp precision).
    /// Multiple frames may share the same value. Reads during live-mode idle
    /// return the same value as immediately after the last frame applied
    /// (principle 5 — frame-driven advancement, no continuous-time read).
    /// </summary>
    DateTimeOffset Now { get; }

    /// <summary>
    /// Strictly-monotonic frame index. Ticks once per applied frame.
    /// Identifies a unique point in the trajectory; tie-breaks within a
    /// wall-clock second; pairs with <see cref="Now"/> as the full identity
    /// of a simulated moment (principle 5).
    /// </summary>
    long Frame { get; }

    /// <summary>
    /// Current world mode. <see cref="WorldMode.Replaying"/> while draining
    /// recorded frames toward the live tail; <see cref="WorldMode.Live"/>
    /// once caught up. Transition emits a <c>ModeChanged</c> domain event on
    /// the bus. Side-effect-emitting consumers gate on <c>Mode == Live</c>
    /// (principle 12).
    /// </summary>
    WorldMode Mode { get; }
}
