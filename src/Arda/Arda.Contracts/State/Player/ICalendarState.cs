namespace Arda.World.Player;

/// <summary>
/// Read-only view of the calendar's current time state.
/// </summary>
public interface ICalendarState
{
    /// <summary>The last observed timestamp from the log stream.</summary>
    DateTimeOffset? LastTimestamp { get; }

    /// <summary>The current in-game time-of-day shift slug (e.g. "morning", "dusk").</summary>
    string? CurrentShift { get; }
}
