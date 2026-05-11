using FluentAssertions;
using Mithril.GameState.Sessions;
using Xunit;

namespace Mithril.GameState.Tests.Sessions;

public class LoginBannerParserTests
{
    [Fact]
    public void Parses_canonical_banner()
    {
        var line = "[12:25:04] Logged in as character Emraell. Time UTC=05/11/2026 12:25:04. Timezone Offset 01:00:00";

        LoginBannerParser.TryParse(line, out var session).Should().BeTrue();

        session.CharacterName.Should().Be("Emraell");
        session.LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
        session.LoggedInUtc.Kind.Should().Be(DateTimeKind.Utc);
        session.TimezoneOffset.Should().Be(TimeSpan.FromHours(1));
        session.SessionId.Should().Be($"Emraell|{session.LoggedInUtc:O}");
    }

    [Fact]
    public void Parses_banner_without_log_prefix()
    {
        var line = "Logged in as character Foo. Time UTC=1/2/2026 3:4:5. Timezone Offset 00:00:00";

        LoginBannerParser.TryParse(line, out var session).Should().BeTrue();

        session.CharacterName.Should().Be("Foo");
        session.LoggedInUtc.Should().Be(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        session.TimezoneOffset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Parses_negative_offset()
    {
        var line = "[00:00:00] Logged in as character Bar. Time UTC=12/31/2025 23:59:59. Timezone Offset -05:00:00";

        LoginBannerParser.TryParse(line, out var session).Should().BeTrue();

        session.TimezoneOffset.Should().Be(TimeSpan.FromHours(-5));
    }

    [Fact]
    public void Same_banner_collapses_to_same_session_id()
    {
        var line = "[12:25:04] Logged in as character Emraell. Time UTC=05/11/2026 12:25:04. Timezone Offset 01:00:00";

        LoginBannerParser.TryParse(line, out var first).Should().BeTrue();
        LoginBannerParser.TryParse(line, out var second).Should().BeTrue();

        second.SessionId.Should().Be(first.SessionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("LocalPlayer: ProcessAddPlayer(123, 456, \"\", \"Emraell\")")]
    [InlineData("[12:25:04] Logged in as character . Time UTC=05/11/2026 12:25:04. Timezone Offset 01:00:00")]
    [InlineData("[12:25:04] Logged in as character Emraell. Time UTC=not-a-date. Timezone Offset 01:00:00")]
    [InlineData("[12:25:04] Logged in as character Emraell. Time UTC=05/11/2026 12:25:04. Timezone Offset garbage")]
    public void Rejects_non_banner_lines(string line)
    {
        LoginBannerParser.TryParse(line, out _).Should().BeFalse();
    }
}
