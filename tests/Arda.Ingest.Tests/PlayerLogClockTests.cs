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

        // Real game format: invariant-culture MM/dd/yyyy HH:mm:ss (slashes,
        // month-first), followed by ". Timezone Offset ...".
        clock.TryConsumeBanner(
            "[14:30:05] Logged in as character Bob. Time UTC=01/15/2026 14:30:05. Timezone Offset 00:00:00".AsSpan());

        var result = clock.TryParse("[14:30:06] LocalPlayer: action".AsSpan());

        result.HasTimestamp.Should().BeTrue();
        result.Timestamp!.Value.Year.Should().Be(2026);
        result.Timestamp!.Value.Month.Should().Be(1);
        result.Timestamp!.Value.Day.Should().Be(15);
    }

    [Fact]
    public void TryConsumeBanner_CapturedRealBanner_AnchorsToCorrectDate()
    {
        var clock = new PlayerLogClock(TimeProvider.System);

        // Verbatim line captured from a live Player.log (issue #942).
        clock.TryConsumeBanner(
            "[12:55:00] Logged in as character Emraell. Time UTC=05/31/2026 12:55:00. Timezone Offset 01:00:00".AsSpan());

        var result = clock.TryParse("[12:55:01] LocalPlayer: action".AsSpan());

        result.HasTimestamp.Should().BeTrue();
        result.Timestamp!.Value.Year.Should().Be(2026);
        result.Timestamp!.Value.Month.Should().Be(5);
        result.Timestamp!.Value.Day.Should().Be(31);
        result.Timestamp!.Value.Offset.Should().Be(TimeSpan.Zero,
            "Time UTC= is already UTC; the trailing Timezone Offset applies to chat-local conversion, not Player.log");
    }

    [Fact]
    public void TryConsumeBanner_NewBannerResetsDate_NoFalseMidnightAdvance()
    {
        var clock = new PlayerLogClock(TimeProvider.System);

        clock.TryConsumeBanner(
            "[14:30:05] Logged in as character Bob. Time UTC=01/15/2026 14:30:05. Timezone Offset 00:00:00".AsSpan());
        clock.TryParse("[14:30:06] line".AsSpan());

        // Re-login: a new banner with an earlier time-of-day on a new date.
        clock.TryConsumeBanner(
            "[10:00:00] Logged in as character Bob. Time UTC=01/20/2026 10:00:00. Timezone Offset 00:00:00".AsSpan());

        var result = clock.TryParse("[10:00:01] post-relogin".AsSpan());

        result.HasTimestamp.Should().BeTrue();
        result.Timestamp!.Value.Year.Should().Be(2026);
        result.Timestamp!.Value.Month.Should().Be(1);
        result.Timestamp!.Value.Day.Should().Be(20,
            "Reset() inside TryConsumeBanner clears _prevTimeOfDay so the earlier HH:MM:SS does not trigger a phantom midnight advance");
    }

    [Fact]
    public void EnsureAnchored_LiveLogLastLineAheadOfMtime_DoesNotRollBackADay()
    {
        var clock = new PlayerLogClock(TimeProvider.System);
        var (buffer, lines) = BuildBatch("[12:56:20] first", "[12:56:30] last");

        // Live log: the newest buffered line (12:56:30) is a few seconds ahead
        // of the file's recorded last-write-time (12:56:25). This must NOT roll
        // the anchor date back a full day (issue #942, root cause #2).
        var mtime = new DateTime(2026, 5, 31, 12, 56, 25, DateTimeKind.Utc);

        clock.EnsureAnchored(lines, buffer, () => mtime);

        var result = clock.TryParse("[12:56:31] next".AsSpan());

        result.HasTimestamp.Should().BeTrue();
        result.Timestamp!.Value.Date.Should().Be(new DateTime(2026, 5, 31),
            "a live-log last line marginally ahead of the file mtime must not subtract a whole day");
    }

    [Fact]
    public void EnsureAnchored_LastLineGenuinelyPriorDay_RollsBackADay()
    {
        var clock = new PlayerLogClock(TimeProvider.System);
        // Last log line late at night; file not written again until well into
        // the next morning (mtime). The line genuinely belongs to the prior day.
        var (buffer, lines) = BuildBatch("[23:55:00] late night line");
        var mtime = new DateTime(2026, 5, 31, 06, 00, 00, DateTimeKind.Utc);

        clock.EnsureAnchored(lines, buffer, () => mtime);

        var result = clock.TryParse("[23:55:01] next".AsSpan());

        result.HasTimestamp.Should().BeTrue();
        result.Timestamp!.Value.Date.Should().Be(new DateTime(2026, 5, 30),
            "a last line hours ahead of the mtime time-of-day genuinely belongs to the previous day");
    }

    private static (char[] buffer, (int Start, int Length)[] lines) BuildBatch(params string[] textLines)
    {
        var sb = new System.Text.StringBuilder();
        var spans = new (int Start, int Length)[textLines.Length];
        for (var i = 0; i < textLines.Length; i++)
        {
            var start = sb.Length;
            sb.Append(textLines[i]);
            spans[i] = (start, textLines[i].Length);
        }

        return (sb.ToString().ToCharArray(), spans);
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
