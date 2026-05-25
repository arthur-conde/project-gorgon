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
}
