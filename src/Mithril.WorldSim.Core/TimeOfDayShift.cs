namespace Mithril.WorldSim;

/// <summary>
/// Project-Gorgon in-game time-of-day shift transition (Midnight / Dawn /
/// Morning / Afternoon / Dusk / Night). Emitted by a composer on the
/// PlayerWorld bus whenever a <see cref="CalendarTimeAdvanced"/> tick crosses
/// the boundary between two consecutive shift buckets — at most one emission
/// per real-world bucket transition, regardless of how many calendar ticks
/// landed inside the same bucket.
///
/// <para><b>Subscribers.</b> Gandalf's shift-alarm path (scheduler-collapse
/// migration item #12) subscribes via
/// <c>IWorldEventBus.Subscribe&lt;TimeOfDayShift&gt;(…)</c>, gates on
/// <see cref="Mode"/> == <see cref="WorldMode.Live"/> at the side-effect
/// boundary, and plays the corresponding per-shift alarm. State-deriving
/// subscribers (countdown chip, future calendars) can ignore <see cref="Mode"/>
/// and use <see cref="At"/> directly.</para>
///
/// <para><b>Why a composer-derived event, not a folder-applied one.</b> Shift
/// transitions are not in the source stream — they are a *projection* of the
/// world clock onto PG's published shift table (the same table the shell's
/// "Next: Dawn in 4m 23s" chip uses). The folder/composer split puts
/// projection on the composer side (principle 10) so the source-stream
/// shape stays minimal and PG-shape-changes (a Gorgon patch retuning the
/// shift table) reshape exactly one composer rather than every consumer.</para>
///
/// <para>Ratified in
/// <a href="https://github.com/moumantai-gg/mithril/issues/613">#613</a>
/// (Gandalf scheduler collapse, Phase 4 of the world-sim migration).</para>
/// </summary>
/// <param name="From">
/// The shift the world was in immediately before this transition. <c>null</c>
/// only for the very first emission after a world starts — there is no
/// "previous" shift on cold-start, so the first tick that observes a shift
/// reports <c>From == null</c> and <c>To</c> = the current shift.
/// </param>
/// <param name="To">
/// The shift the world is in at <see cref="At"/>. Always non-null.
/// </param>
/// <param name="At">
/// Simulated wall-clock at which the transition was observed — the timestamp
/// of the <see cref="CalendarTimeAdvanced"/> tick that drove the crossing.
/// Resolution is one wall-clock second (the calendar-tick dedup grain).
/// </param>
/// <param name="Mode">
/// World mode at this tick. Side-effect-emitting subscribers gate on
/// <see cref="WorldMode.Live"/>; state-deriving subscribers ignore it.
/// </param>
public readonly record struct TimeOfDayShift(
    string? From,
    string To,
    DateTimeOffset At,
    WorldMode Mode) : IChangeEvent;
