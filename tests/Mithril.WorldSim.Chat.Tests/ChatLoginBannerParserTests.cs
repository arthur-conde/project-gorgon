using FluentAssertions;
using Mithril.WorldSim.Chat.Producers;
using Xunit;

namespace Mithril.WorldSim.Chat.Tests;

public sealed class ChatLoginBannerParserTests
{
    [Fact]
    public void Extracts_character_server_and_offset_from_canonical_banner()
    {
        // PG's canonical banner shape captured from a real chat log
        // (see ChatLogClock.cs and ChatLogRulesTests for corroboration).
        var line = "26-05-19 21:01:14\t**************************************** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00.";

        ChatLoginBannerParser.TryParse(line, out var banner).Should().BeTrue();
        banner.Character.Should().Be("Emraell");
        banner.Server.Should().Be("Laeth");
        banner.Offset.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Captures_negative_offset()
    {
        var line = "26-05-19 09:36:04\t**************************************** Logged In As Praxi. Server Laeth. Timezone Offset -07:00:00.";

        ChatLoginBannerParser.TryParse(line, out var banner).Should().BeTrue();
        banner.Offset.Should().Be(TimeSpan.FromHours(-7));
    }

    [Theory]
    [InlineData("26-05-19 21:01:14\t[Trade] Foo: WTS something")]
    [InlineData("26-05-19 21:01:14\t[Status] X x5 added to inventory.")]
    [InlineData("")]
    [InlineData("Logged in as character Emraell. Time UTC=05/19/2026 20:01:14. Timezone Offset 01:00:00")]  // Player-side banner (different capitalisation), should NOT match
    public void Rejects_non_banner_lines(string line)
    {
        ChatLoginBannerParser.TryParse(line, out var banner).Should().BeFalse();
        banner.Server.Should().BeNull();
        banner.Character.Should().BeNull();
    }

    [Fact]
    public void Rejects_malformed_offset()
    {
        var line = "26-05-19 21:01:14\t**** Logged In As Emraell. Server Laeth. Timezone Offset garbage.";
        ChatLoginBannerParser.TryParse(line, out _).Should().BeFalse();
    }
}
