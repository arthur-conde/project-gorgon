namespace Mithril.Shared.Game;

/// <summary>
/// One named portion of the in-game day. <see cref="StartHour"/> is the
/// in-game hour at which the shift begins; the shift runs until the next
/// shift's start hour.
/// </summary>
public sealed record ShiftDefinition(string Slug, string Label, string Emoji, int StartHour);

/// <summary>
/// The six named in-game-time-of-day shifts published by Project Gorgon's
/// community tooling.
///
/// <para>Source: <c>https://pgemissary.com/static/js/game_clock.js</c> — the
/// <c>TIME_OF_DAY</c> constant. Snapshotted here on 2026-05-09 because
/// pgemissary publishes the table only as a JS constant on a page that
/// loads dynamically — there's no API surface to fetch it from at runtime.
/// If pgemissary updates the schedule (re-anchoring a shift's start hour,
/// renaming a label), mirror the change here in coordination with
/// whatever Gorgon release prompts it. Same trust model as the in-game
/// clock anchor in <see cref="GameClock"/>.</para>
/// </summary>
public static class TimeOfDayShifts
{
    public static readonly IReadOnlyList<ShiftDefinition> All =
    [
        new("midnight",  "Midnight",  "\U0001F311",            0),  // 0:00–4:59
        new("dawn",      "Dawn",      "\U0001F305",            5),  // 5:00–7:59
        new("morning",   "Morning",   "☀️",          8),  // 8:00–11:59
        new("afternoon", "Afternoon", "\U0001F324️",     12),  // 12:00–16:59
        new("dusk",      "Dusk",      "\U0001F307",           17),  // 17:00–19:59
        new("night",     "Night",     "\U0001F319",           20),  // 20:00–23:59
    ];

    /// <summary>
    /// The earliest real-time instant ≥ <paramref name="floor"/> at which
    /// any shift's <c>StartHour</c> is reached, plus the shift definition
    /// that begins at that moment. Built on
    /// <see cref="IGameClock.NextOccurrence"/>: each shift's transition is
    /// "the next real-time moment the in-game clock reads <c>StartHour:00</c>",
    /// and we return the soonest across all six.
    /// </summary>
    public static (DateTimeOffset At, ShiftDefinition Shift) NextTransition(
        IGameClock clock, DateTimeOffset floor)
    {
        DateTimeOffset? soonestAt = null;
        ShiftDefinition? soonestShift = null;
        foreach (var shift in All)
        {
            var at = clock.NextOccurrence(new GameTimeOfDay(shift.StartHour, 0), floor);
            if (soonestAt is null || at < soonestAt)
            {
                soonestAt = at;
                soonestShift = shift;
            }
        }
        return (soonestAt!.Value, soonestShift!);
    }
}
