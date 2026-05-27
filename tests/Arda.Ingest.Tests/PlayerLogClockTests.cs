using Arda.Ingest.Clock;
using FluentAssertions;
using Xunit;

namespace Arda.Ingest.Tests;

public class PlayerLogClockTests
{
    [Fact]
    public void TryParse_ValidPrefix_ReturnsTimestampAndConsumedLength()
    {
        var clock = new PlayerLogClock(TimeProvider.System);
        var line = "[14:30:05] LocalPlayer: ProcessAddPlayer(...)".AsSpan();

        var result = clock.TryParse(line);

        result.HasTimestamp.Should().BeTrue();
        result.ConsumedLength.Should().Be(PlayerLogClock.PrefixLength);
        result.Timestamp!.Value.Hour.Should().Be(14);
        result.Timestamp!.Value.Minute.Should().Be(30);
        result.Timestamp!.Value.Second.Should().Be(5);
        result.Timestamp!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No prefix here")]
    [InlineData("[invalid] text")]
    [InlineData("[25:61:99] overflow")]
    public void TryParse_InvalidPrefix_ReturnsNone(string line)
    {
        var clock = new PlayerLogClock(TimeProvider.System);

        var result = clock.TryParse(line.AsSpan());

        result.HasTimestamp.Should().BeFalse();
        result.ConsumedLength.Should().Be(0);
    }

    [Fact]
    public void TryParse_MidnightRollover_AdvancesDate()
    {
        var clock = new PlayerLogClock(TimeProvider.System);

        var before = clock.TryParse("[23:59:58] something".AsSpan());
        var after = clock.TryParse("[00:00:01] something else".AsSpan());

        after.HasTimestamp.Should().BeTrue();
        after.Timestamp!.Value.Date.Should().Be(before.Timestamp!.Value.Date.AddDays(1));
    }

    [Fact]
    public void TryConsumeBanner_AnchorsToEmbeddedUtcDate()
    {
        var clock = new PlayerLogClock(TimeProvider.System);

        clock.TryConsumeBanner(
            "[14:30:05] Logged in as character Bob. Time UTC=2026-01-15 14:30:05".AsSpan());

        var result = clock.TryParse("[14:30:06] LocalPlayer: action".AsSpan());

        result.HasTimestamp.Should().BeTrue();
        result.Timestamp!.Value.Year.Should().Be(2026);
        result.Timestamp!.Value.Month.Should().Be(1);
        result.Timestamp!.Value.Day.Should().Be(15);
    }

    [Fact]
    public void TryConsumeBanner_NewBannerResetsDate_NoFalseMidnightAdvance()
    {
        var clock = new PlayerLogClock(TimeProvider.System);

        clock.TryConsumeBanner(
            "[14:30:05] Logged in as character Bob. Time UTC=2026-01-15 14:30:05".AsSpan());
        clock.TryParse("[14:30:06] line".AsSpan());

        // Re-login: a new banner with an earlier time-of-day on a new date.
        clock.TryConsumeBanner(
            "[10:00:00] Logged in as character Bob. Time UTC=2026-01-20 10:00:00".AsSpan());

        var result = clock.TryParse("[10:00:01] post-relogin".AsSpan());

        result.HasTimestamp.Should().BeTrue();
        result.Timestamp!.Value.Year.Should().Be(2026);
        result.Timestamp!.Value.Month.Should().Be(1);
        result.Timestamp!.Value.Day.Should().Be(20,
            "Reset() inside TryConsumeBanner clears _prevTimeOfDay so the earlier HH:MM:SS does not trigger a phantom midnight advance");
    }

    [Fact]
    public void TryConsumeBanner_NonBannerLine_NoEffect()
    {
        var clock = new PlayerLogClock(TimeProvider.System);

        clock.TryConsumeBanner("[14:30:05] LocalPlayer: ProcessAddItem(1)".AsSpan());

        // No date set — TryParse falls back to wall-clock (today).
        var result = clock.TryParse("[14:30:06] x".AsSpan());
        result.HasTimestamp.Should().BeTrue();
        result.Timestamp!.Value.Date.Should().Be(DateTime.UtcNow.Date);
    }
}
