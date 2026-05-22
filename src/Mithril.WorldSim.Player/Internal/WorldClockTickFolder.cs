namespace Mithril.WorldSim.Player.Internal;

/// <summary>
/// Owned by <see cref="Producers.WorldClockTickProducer"/>. Consumes
/// <see cref="WorldClockTick"/> frames and emits
/// <see cref="CalendarTimeAdvanced"/> change events at one-per-wall-clock-second
/// cadence (principle 13 — "deduplicated within a wall-clock second"). Bus
/// surfacing happens automatically via the world's
/// <c>PublishChangeEvent</c> path; subscribers see
/// <c>Frame&lt;CalendarTimeAdvanced&gt;</c> on the world bus.
///
/// <para>The folder still receives every tick (so the world clock advances at
/// the source-stream cadence and tied-timestamp folder dispatch ordering is
/// preserved); it just suppresses the bus-visible emission when the new tick
/// shares a second with the previous emission. Consumers therefore see the
/// rate of advancement (one event per second of simulated time), not the rate
/// of the source stream (which during busy stretches can be hundreds of
/// lines per second).</para>
///
/// <para><b>Mode comes from the clock, not the tick payload.</b> The world's
/// merger flips <see cref="IWorldClock.Mode"/> between dispatches (see
/// <c>PlayerWorld.UpdateModeIfReady</c>); the folder reads <c>clock.Mode</c>
/// at emission time so the carried <see cref="CalendarTimeAdvanced.Mode"/>
/// matches the world's mode at that exact frame — the same property the
/// folder contract guarantees for any change event.</para>
/// </summary>
internal sealed class WorldClockTickFolder : IFolder<WorldClockTick>
{
    /// <summary>
    /// Last second (UTC ticks rounded to one-second resolution) at which a
    /// <see cref="CalendarTimeAdvanced"/> was emitted. <c>null</c> means
    /// "no emission yet" — the very first tick always emits.
    /// </summary>
    private long? _lastEmittedSecondTicks;

    public IReadOnlyList<IChangeEvent> Apply(Frame<WorldClockTick> frame, IWorldClock clock)
    {
        // Compare seconds via UTC ticks rather than .Second so we don't false-
        // collapse across minute / hour / day boundaries — e.g., 12:00:59
        // followed by 12:01:00 must emit a second tick even though
        // .Second == 59 → 0 wraps. Truncating to the second's epoch tick
        // value gives a single comparable scalar.
        var secondTicks = SecondTicks(frame.Payload.At);
        if (_lastEmittedSecondTicks == secondTicks)
        {
            return Array.Empty<IChangeEvent>();
        }

        _lastEmittedSecondTicks = secondTicks;
        return new IChangeEvent[]
        {
            new CalendarTimeAdvanced(clock.Now, clock.Mode),
        };
    }

    private static long SecondTicks(DateTimeOffset value)
        => value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond);
}
