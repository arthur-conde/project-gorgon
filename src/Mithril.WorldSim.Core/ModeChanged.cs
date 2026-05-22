namespace Mithril.WorldSim;

/// <summary>
/// Domain frame payload emitted on a world's bus when the world transitions
/// between <see cref="WorldMode.Replaying"/> and <see cref="WorldMode.Live"/>
/// (principle 12). Each world emits its own <c>Frame&lt;ModeChanged&gt;</c>
/// independently — PlayerWorld may catch up at T1, ChatWorld at T2.
///
/// <para>Side-effect-emitting consumers gate on <c>Mode == Live</c>; the
/// <see cref="ModeChanged"/> frame is the structural signal they use to
/// re-arm or arm-for-the-first-time. State-deriving consumers (folders,
/// composers, views) ignore mode changes — their behaviour is mode-agnostic
/// by contract.</para>
///
/// <para><b>Emission contract:</b> a <see cref="ModeChanged"/> frame is
/// emitted only when the world actually experienced the source mode — i.e.,
/// at least one frame was applied while <c>Mode == Replaying</c>. A world
/// that starts <see cref="WorldMode.Live"/> (no mode-aware producers, every
/// mode-aware producer's <c>ReachedLive</c> already complete at start, or the
/// source stream's replay phase is empty) emits no <see cref="ModeChanged"/>
/// — consumers read <see cref="IWorldClock.Mode"/> for the initial state and
/// subscribe to the bus only for actual transitions.</para>
/// </summary>
/// <param name="From">The mode the world was in before the transition.</param>
/// <param name="To">The mode the world is in after the transition.</param>
/// <param name="At">
/// Simulated wall-clock at the moment of transition
/// (<see cref="IWorldClock.Now"/> of the world's clock at the transition
/// point — the timestamp of the most recently applied frame before the flip).
/// </param>
public readonly record struct ModeChanged(WorldMode From, WorldMode To, DateTimeOffset At);
