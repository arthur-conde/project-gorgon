using System;
using FluentAssertions;
using Mithril.Shared.Game;
using Xunit;

namespace Mithril.Shared.Tests;

public class GameClockTests
{
    // Pgemissary's published anchor: 2026-03-11T01:45:01.212304Z = 9:00 PM (75600 sec).
    // The tests below feed a TimeProvider so they don't depend on wall-clock time.
    private static readonly DateTime PgEmissaryAnchorUtc =
        new(2026, 3, 11, 1, 45, 1, 212, DateTimeKind.Utc);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public void AtAnchor_Returns9PM()
    {
        var clock = new GameClock(new FixedTimeProvider(PgEmissaryAnchorUtc));
        clock.GetCurrent().Should().Be(new GameTimeOfDay(21, 0));
    }

    [Fact]
    public void FiveRealMinutesLater_AdvancesOneInGameHour()
    {
        var clock = new GameClock(new FixedTimeProvider(PgEmissaryAnchorUtc.AddMinutes(5)));
        clock.GetCurrent().Should().Be(new GameTimeOfDay(22, 0));
    }

    [Fact]
    public void TwoRealHoursLater_WrapsToSameTimeOfDay()
    {
        // 2 real hours = 24 in-game hours = full in-game day.
        var clock = new GameClock(new FixedTimeProvider(PgEmissaryAnchorUtc.AddHours(2)));
        clock.GetCurrent().Should().Be(new GameTimeOfDay(21, 0));
    }

    [Fact]
    public void BeforeAnchor_HandlesNegativeElapsedWithoutNegativeMinutes()
    {
        // Elapsed is negative; the modulo bookkeeping should still produce 0–23.
        var clock = new GameClock(new FixedTimeProvider(PgEmissaryAnchorUtc.AddMinutes(-5)));
        clock.GetCurrent().Should().Be(new GameTimeOfDay(20, 0));
    }

    [Theory]
    [InlineData(0, 0, "12:00 AM")]
    [InlineData(0, 5, "12:05 AM")]
    [InlineData(11, 59, "11:59 AM")]
    [InlineData(12, 0, "12:00 PM")]
    [InlineData(13, 7, "1:07 PM")]
    [InlineData(23, 0, "11:00 PM")]
    public void ToString12Hour_FormatsCorrectly(int hour, int minute, string expected)
    {
        new GameTimeOfDay(hour, minute).ToString12Hour().Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 0, "00:00")]
    [InlineData(0, 5, "00:05")]
    [InlineData(8, 7, "08:07")]
    [InlineData(13, 30, "13:30")]
    [InlineData(23, 59, "23:59")]
    public void ToString24Hour_FormatsCorrectly(int hour, int minute, string expected)
    {
        new GameTimeOfDay(hour, minute).ToString24Hour().Should().Be(expected);
    }

    [Theory]
    [InlineData(false, 13, 30, "1:30 PM")]
    [InlineData(true, 13, 30, "13:30")]
    [InlineData(false, 0, 0, "12:00 AM")]
    [InlineData(true, 0, 0, "00:00")]
    public void Format_routes_through_the_correct_format(bool use24Hour, int hour, int minute, string expected)
    {
        new GameTimeOfDay(hour, minute).Format(use24Hour).Should().Be(expected);
    }

    [Fact]
    public void NextOccurrence_TargetEqualsCurrent_AdvancesFullCycle()
    {
        // At the anchor, in-game time is 9:00 PM. Asking for the next 9:00 PM
        // should jump one full real cycle (7200 s) forward — the alternative
        // would silently fire-on-arm for a 9:00 PM alarm started at 9:00 PM.
        var floor = new DateTimeOffset(PgEmissaryAnchorUtc, TimeSpan.Zero);
        var clock = new GameClock(new FixedTimeProvider(PgEmissaryAnchorUtc));
        var next = clock.NextOccurrence(new GameTimeOfDay(21, 0), floor);
        (next - floor).Should().Be(TimeSpan.FromSeconds(7200));
    }

    [Fact]
    public void NextOccurrence_TargetSoonAfterCurrent_RoundTripsThroughGetCurrent()
    {
        // 5 real minutes after anchor → in-game 10:00 PM. Asking for next
        // 11:00 PM should land 5 real minutes further on (= 10 min after anchor),
        // and a clock fixed at that instant should report exactly 11:00 PM.
        var floor = new DateTimeOffset(PgEmissaryAnchorUtc.AddMinutes(5), TimeSpan.Zero);
        var clock = new GameClock(new FixedTimeProvider(floor.UtcDateTime));
        var next = clock.NextOccurrence(new GameTimeOfDay(23, 0), floor);
        (next - floor).Should().Be(TimeSpan.FromMinutes(5));
        new GameClock(new FixedTimeProvider(next.UtcDateTime))
            .GetCurrent().Should().Be(new GameTimeOfDay(23, 0));
    }

    [Fact]
    public void NextOccurrence_TargetJustPassed_ReturnsNearlyFullCycle()
    {
        // Floor is one tick after the anchor's 9:00 PM moment → "next 9:00 PM"
        // should be almost a full real cycle out (7200 s minus one tick).
        var floor = new DateTimeOffset(PgEmissaryAnchorUtc, TimeSpan.Zero) + TimeSpan.FromTicks(1);
        var clock = new GameClock(new FixedTimeProvider(floor.UtcDateTime));
        var next = clock.NextOccurrence(new GameTimeOfDay(21, 0), floor);
        (next - floor).Should().Be(TimeSpan.FromSeconds(7200) - TimeSpan.FromTicks(1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 0)]
    [InlineData(12, 0)]
    [InlineData(18, 0)]
    [InlineData(3, 27)]
    [InlineData(20, 59)]
    public void NextOccurrence_RoundTrip_ManyTargets(int hour, int minute)
    {
        // For an arbitrary floor strictly after anchor, the returned instant
        // must satisfy GetCurrent(returned) == target — modulo the 1-minute
        // resolution of GameTimeOfDay.
        var floor = new DateTimeOffset(PgEmissaryAnchorUtc, TimeSpan.Zero).AddMinutes(17.3);
        var clock = new GameClock(new FixedTimeProvider(floor.UtcDateTime));
        var target = new GameTimeOfDay(hour, minute);
        var next = clock.NextOccurrence(target, floor);

        next.Should().BeOnOrAfter(floor);
        new GameClock(new FixedTimeProvider(next.UtcDateTime))
            .GetCurrent().Should().Be(target);
    }

    [Fact]
    public void NextOccurrence_FloorBeforeAnchor_HandlesNegativeElapsed()
    {
        // Inverse formula must cope with floors that pre-date the anchor — same
        // negative-modulo case GetCurrent handles.
        var floor = new DateTimeOffset(PgEmissaryAnchorUtc.AddHours(-3), TimeSpan.Zero);
        var clock = new GameClock(new FixedTimeProvider(floor.UtcDateTime));
        var next = clock.NextOccurrence(new GameTimeOfDay(0, 0), floor);
        next.Should().BeOnOrAfter(floor);
        new GameClock(new FixedTimeProvider(next.UtcDateTime))
            .GetCurrent().Should().Be(new GameTimeOfDay(0, 0));
    }

    [Fact]
    public void OurTickFlipObservation_AgreesWithinSeconds()
    {
        // On 2026-05-04T21:00:08.78Z we observed the in-game clock flip to 12:00 PM.
        // Confirms pgemissary's anchor matches what we manually captured.
        var clock = new GameClock(new FixedTimeProvider(
            new DateTime(2026, 5, 4, 21, 0, 8, 780, DateTimeKind.Utc)));
        var t = clock.GetCurrent();
        t.Hour.Should().Be(12);
        t.Minute.Should().BeInRange(0, 1); // ~1 min slop from cross-anchor drift
    }
}
