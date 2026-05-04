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
