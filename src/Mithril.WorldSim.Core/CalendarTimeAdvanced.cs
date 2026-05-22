namespace Mithril.WorldSim;

/// <summary>
/// Canonical time-progression signal on a world's bus (principle 13 — "calendar
/// time is a domain event, not a clock read"). Emitted by the world-clock-tick
/// folder owned by every world; carries the world clock's <c>Now</c> and
/// <c>Mode</c> at emission. Deduplicated within a wall-clock second so
/// consumers see one tick per second of advancement, never N per second of
/// per-line cadence.
///
/// <para><b>Subscribers.</b> Module-side schedulers (Gandalf timer alarms,
/// Samwise ripeness alarms — see <c>docs/world-simulator.md</c> migration
/// item #12) subscribe via <c>IWorldEventBus.Subscribe&lt;CalendarTimeAdvanced&gt;(…)</c>;
/// compare their internal thresholds against <see cref="Now"/>; fire (gated
/// on <see cref="Mode"/> == <see cref="WorldMode.Live"/>). No real-wall-clock
/// leak — the timestamp is always the source-stream's reported time, replay-
/// deterministic.</para>
///
/// <para><b>Per-world cadence.</b> Emitted at the source-stream cadence
/// (one tick per second the source advances), independently per world.
/// PlayerWorld emits from its own log tail; ChatWorld (Phase 2+) will emit
/// from its own. Consumers fusing across worlds should pick one as the
/// scheduling driver — typically PlayerWorld, since its source is the busier
/// stream — rather than awaiting both.</para>
///
/// <para>Ratified in <a href="https://github.com/moumantai-gg/mithril/issues/644">#644</a>,
/// implemented in <a href="https://github.com/moumantai-gg/mithril/issues/655">#655</a>.
/// Domain glossary: see <c>docs/world-simulator.md</c> "Canonical time-related
/// domain events".</para>
/// </summary>
/// <param name="Now">
/// Simulated wall-clock at this tick — the timestamp of the source-stream
/// frame that drove this advancement (same value as
/// <see cref="IWorldClock.Now"/> at emission).
/// </param>
/// <param name="Mode">
/// World mode at this tick. Side-effect-emitting subscribers gate on
/// <see cref="WorldMode.Live"/>; state-deriving subscribers ignore it.
/// </param>
public readonly record struct CalendarTimeAdvanced(
    DateTimeOffset Now,
    WorldMode Mode) : IChangeEvent;
