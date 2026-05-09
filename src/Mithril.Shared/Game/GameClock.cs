using System;

namespace Mithril.Shared.Game;

/// <summary>
/// In-game time-of-day for Project Gorgon. The game has a 24h day with AM/PM,
/// no calendar, and time advances at 12× wall-clock (1 in-game hour = 5 real minutes).
/// </summary>
public interface IGameClock
{
    /// <summary>Current in-game time of day.</summary>
    GameTimeOfDay GetCurrent();

    /// <summary>
    /// Earliest real-time instant ≥ <paramref name="floor"/> at which the in-game
    /// clock reads <paramref name="target"/>. The in-game day is 7200 real seconds
    /// long, so occurrences recur on a 7200-second real-time grid.
    /// </summary>
    /// <remarks>
    /// Edge: when <paramref name="floor"/> sits at the target instant (or within
    /// ~50 ms before it), the result is bumped one full in-game day forward. This
    /// prevents fire-on-Start for an alarm started at exactly its target time —
    /// the user expects "next 6 PM," not "right now," and a recurring alarm would
    /// otherwise double-fire at arm time.
    /// </remarks>
    DateTimeOffset NextOccurrence(GameTimeOfDay target, DateTimeOffset floor);
}

/// <summary>
/// In-game hour and minute (0–23, 0–59). The game UI exposes only the hour, so
/// minute precision derives from the anchor + ratio rather than from observation.
/// </summary>
public readonly record struct GameTimeOfDay(int Hour, int Minute)
{
    public string ToString12Hour()
    {
        var h12 = Hour % 12;
        if (h12 == 0) h12 = 12;
        var ampm = Hour < 12 ? "AM" : "PM";
        return $"{h12}:{Minute:D2} {ampm}";
    }
}

public sealed class GameClock : IGameClock
{
    // Anchor sourced from https://pgemissary.com/api/game-clocks (all PG servers share
    // the same in-game clock). Independently verified against a manual tick-flip
    // capture on 2026-05-04 — agreed within ~8 real seconds. Re-anchor here if a
    // future patch changes the day/night cycle.
    private static readonly DateTime AnchorUtc =
        new(2026, 3, 11, 1, 45, 1, 212, DateTimeKind.Utc);
    private const int AnchorGameSeconds = 75600; // 9:00 PM

    private const int Ratio = 12;
    private const int SecondsPerGameDay = 86400;

    private readonly TimeProvider _time;

    public GameClock(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public GameTimeOfDay GetCurrent()
    {
        var elapsedReal = (_time.GetUtcNow().UtcDateTime - AnchorUtc).TotalSeconds;
        var gameSecs = (AnchorGameSeconds + elapsedReal * Ratio) % SecondsPerGameDay;
        if (gameSecs < 0) gameSecs += SecondsPerGameDay;
        var total = (int)gameSecs;
        return new GameTimeOfDay(total / 3600, total % 3600 / 60);
    }

    public DateTimeOffset NextOccurrence(GameTimeOfDay target, DateTimeOffset floor)
    {
        // One in-game day == 7200 real seconds. Both targets and the anchor align
        // to whole-minute game-second boundaries (multiples of 60), so dividing by
        // Ratio (12) yields exact 5-real-second multiples — no float drift.
        const long RealCycleTicks = 7200L * TimeSpan.TicksPerSecond;
        const long EpsilonTicks = TimeSpan.TicksPerMillisecond * 50;

        long targetGameSecs = target.Hour * 3600L + target.Minute * 60L;
        long diffGameSecs = (targetGameSecs - AnchorGameSeconds) % SecondsPerGameDay;
        if (diffGameSecs < 0) diffGameSecs += SecondsPerGameDay;
        long targetOffsetTicks = diffGameSecs * TimeSpan.TicksPerSecond / Ratio;

        long floorAnchorTicks = (floor.UtcDateTime - AnchorUtc).Ticks;
        long floorOffsetTicks = ((floorAnchorTicks % RealCycleTicks) + RealCycleTicks) % RealCycleTicks;

        long deltaTicks = ((targetOffsetTicks - floorOffsetTicks) % RealCycleTicks + RealCycleTicks) % RealCycleTicks;
        if (deltaTicks < EpsilonTicks) deltaTicks += RealCycleTicks;

        return floor + TimeSpan.FromTicks(deltaTicks);
    }
}
