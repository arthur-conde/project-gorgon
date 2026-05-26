using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the PG in-game time-of-day crosses a shift boundary
/// (e.g. Night -> Midnight, Midnight -> Dawn). Derived from
/// <see cref="CalendarTimeAdvanced"/> timestamps projected through
/// <c>GameClock.Project</c>.
/// </summary>
public readonly record struct TimeOfDayShifted(
    string? From,
    string To,
    DateTimeOffset At,
    LogLineMetadata Metadata);
