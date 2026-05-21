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
/// </summary>
/// <param name="From">The mode the world was in before the transition.</param>
/// <param name="To">The mode the world is in after the transition.</param>
/// <param name="At">
/// Simulated wall-clock at the moment of transition (<see cref="IWorldClock.Now"/>
/// of the world's clock at the transition point — typically the timestamp of
/// the most recently applied replay frame, or the first live frame if no
/// replay phase preceded it).
/// </param>
public readonly record struct ModeChanged(WorldMode From, WorldMode To, DateTimeOffset At);
