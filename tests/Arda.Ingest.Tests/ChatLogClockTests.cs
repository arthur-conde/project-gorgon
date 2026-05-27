using Arda.Ingest.Clock;
using FluentAssertions;
using Xunit;

namespace Arda.Ingest.Tests;

public class ChatLogClockTests
{
    [Fact]
    public void TryParse_ValidPrefix_ReturnsTimestampAndConsumedLength()
    {
        var clock = new ChatLogClock(TimeZoneInfo.Utc);
        var line = "26-05-25 14:30:05\tSome chat message".AsSpan();

        var result = clock.TryParse(line);

        result.HasTimestamp.Should().BeTrue();
        result.ConsumedLength.Should().Be(ChatLogClock.PrefixLength);
        result.Timestamp!.Value.Year.Should().Be(2026);
        result.Timestamp!.Value.Month.Should().Be(5);
        result.Timestamp!.Value.Day.Should().Be(25);
        result.Timestamp!.Value.Hour.Should().Be(14);
        result.Timestamp!.Value.Minute.Should().Be(30);
        result.Timestamp!.Value.Second.Should().Be(5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No prefix here")]
    [InlineData("not-a-date format")]
    public void TryParse_InvalidPrefix_ReturnsNone(string line)
    {
        var clock = new ChatLogClock(TimeZoneInfo.Utc);

        var result = clock.TryParse(line.AsSpan());

        result.HasTimestamp.Should().BeFalse();
        result.ConsumedLength.Should().Be(0);
    }

    [Fact]
    public void SetOffset_OverridesTimezone()
    {
        var clock = new ChatLogClock(TimeZoneInfo.Utc);
        clock.SetOffset(TimeSpan.FromHours(-5));

        var line = "26-05-25 14:30:05\tMessage".AsSpan();
        var result = clock.TryParse(line);

        result.HasTimestamp.Should().BeTrue();
        result.Timestamp!.Value.Offset.Should().Be(TimeSpan.FromHours(-5));
    }
}
