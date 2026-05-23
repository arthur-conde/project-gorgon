using FluentAssertions;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Game;
using Xunit;

namespace Gandalf.Tests;

/// <summary>
/// #711 — pins the wall-clock-anchoring contract on
/// <see cref="TimerProgressService.ComputeFiringAt"/> for
/// <see cref="GandalfTriggerKind.GameTimeOfDay"/> timers. The user-driven
/// "fire at next 6 AM in-game" semantics demand wall-clock anchoring: a user
/// clicking Start during the Replaying drain expects the alarm to fire at the
/// real-world moment PG's in-game clock reads 6 AM, not at a moment offset
/// by however far the world clock currently lags wall-clock.
/// </summary>
public class TimerProgressServiceGameTimeOfDayClockTests
{
    [Fact]
    public void ComputeFiringAt_for_GameTimeOfDay_returns_a_wall_clock_instant_projecting_onto_the_target_in_game_time()
    {
        // Post-#711 DI hands TimerProgressService a wall-clock-anchored
        // IGameClock (TimeProvider.System-backed). Independently of the
        // injected clock's _nowSource, NextOccurrence is pure math on (target,
        // startedAt) — when startedAt is wall-clock (the user-action carve-out
        // in TimerProgressService.Start sets startedAt = _time.GetUtcNow()),
        // the resulting FiringAt projects onto the target in-game time and is
        // strictly after startedAt by at most one in-game day (7200 real
        // seconds = 2 real hours).
        var clock = new GameClock(TimeProvider.System);
        var def = new GandalfTimerDef
        {
            Name = "Fire at next 6 AM",
            Kind = GandalfTriggerKind.GameTimeOfDay,
            GameHour = 6,
            GameMinute = 0,
        };

        var startedAt = DateTimeOffset.UtcNow;
        var firingAt = TimerProgressService.ComputeFiringAt(def, startedAt, clock);

        firingAt.Should().BeAfter(startedAt,
            "the EpsilonTicks guard in NextOccurrence bumps a target == floor result " +
            "one full in-game day forward — Start at 6:00 should fire at the NEXT 6:00, " +
            "not immediately");
        (firingAt - startedAt).Should().BeLessThanOrEqualTo(
            TimeSpan.FromSeconds(7200) + TimeSpan.FromSeconds(1),
            "one in-game day == 7200 real seconds; FiringAt is strictly within one cycle of startedAt");

        // The wall-clock instant `firingAt` projects onto the target's
        // (GameHour, GameMinute). This is the property the user perceives —
        // when wall-clock reaches firingAt, the in-game clock reads 6:00.
        GameClock.Project(firingAt).Should().Be(new GameTimeOfDay(6, 0));
    }

    [Fact]
    public void ComputeFiringAt_for_GameTimeOfDay_is_pure_math_on_startedAt_independent_of_clock_nowSource()
    {
        // The injected clock's _nowSource is only read by GetCurrent; NextOccurrence
        // is pure math on (target, floor=startedAt). This test pins that contract:
        // two clocks with wildly-different _nowSources but the same startedAt yield
        // the same FiringAt. So during the Replaying drain — where PlayerWorld.Clock.Now
        // lags real wall-clock by minutes — a GameTimeOfDay timer started with
        // startedAt = wall-clock still anchors its FiringAt in wall-clock,
        // *independent* of what the world clock currently reads.
        var startedAt = DateTimeOffset.UtcNow;
        var wallAnchoredClock = new GameClock(() => startedAt);
        var laggedAnchoredClock = new GameClock(() => DateTimeOffset.MinValue);

        var def = new GandalfTimerDef
        {
            Name = "Fire at next 8 PM",
            Kind = GandalfTriggerKind.GameTimeOfDay,
            GameHour = 20,
            GameMinute = 0,
        };

        var firingViaWall = TimerProgressService.ComputeFiringAt(def, startedAt, wallAnchoredClock);
        var firingViaLagged = TimerProgressService.ComputeFiringAt(def, startedAt, laggedAnchoredClock);

        firingViaWall.Should().Be(firingViaLagged,
            "NextOccurrence ignores _nowSource — FiringAt anchors in startedAt alone");
        GameClock.Project(firingViaWall).Should().Be(new GameTimeOfDay(20, 0));
    }
}
