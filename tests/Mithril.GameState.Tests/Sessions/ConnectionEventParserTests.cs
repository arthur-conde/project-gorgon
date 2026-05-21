using FluentAssertions;
using Mithril.GameState.Sessions.Parsing;
using Xunit;

namespace Mithril.GameState.Tests.Sessions;

public sealed class ConnectionEventParserTests
{
    private static readonly ConnectionEventParser Parser = new();
    private static readonly DateTime Ts = new(2026, 5, 21, 19, 59, 36, DateTimeKind.Utc);

    /// <summary>
    /// Real Player.log payload (this machine, 2026-05-21), envelope-stripped
    /// per the L0.5 classifier (SystemSignalKind.ConnectionEvent eats the
    /// "EVENT(Ok): " prefix). Kept verbatim so a PG patch that changes the
    /// shape shows up as a test diff.
    /// </summary>
    private const string RealStrippedPayload = "connected, url=s4.projectgorgon.com, port=9002";

    [Fact]
    public void Real_payload_parses_url_and_port()
    {
        var evt = Parser.TryParse(RealStrippedPayload, Ts)
            .Should().BeOfType<ConnectionEvent>().Subject;

        evt.Timestamp.Should().Be(Ts);
        evt.Url.Should().Be("s4.projectgorgon.com");
        evt.Port.Should().Be(9002);
    }

    [Fact]
    public void Raw_prefixed_form_is_accepted_for_ad_hoc_callers()
    {
        // Defensive — production callers pass the L0.5-stripped form, but a
        // test or REPL caller may hand the full "EVENT(Ok): connected, …" line.
        var raw = "EVENT(Ok): " + RealStrippedPayload;
        var evt = Parser.TryParse(raw, Ts)
            .Should().BeOfType<ConnectionEvent>().Subject;

        evt.Url.Should().Be("s4.projectgorgon.com");
        evt.Port.Should().Be(9002);
    }

    [Fact]
    public void Url_is_preserved_verbatim_no_case_normalisation()
    {
        // PG canonicalizes to lowercase, but we don't second-guess — the
        // catalog lookup does case-insensitive matching, so the parser keeps
        // PG's exact string.
        var evt = Parser.TryParse("connected, url=S4.ProjectGorgon.com, port=9002", Ts)
            .Should().BeOfType<ConnectionEvent>().Subject;
        evt.Url.Should().Be("S4.ProjectGorgon.com");
    }

    [Theory]
    [InlineData("connected, url=s0.projectgorgon.com, port=9002", "s0.projectgorgon.com", 9002)]
    [InlineData("connected, url=s1.projectgorgon.com, port=9002", "s1.projectgorgon.com", 9002)]
    [InlineData("connected, url=s4.projectgorgon.com, port=12345", "s4.projectgorgon.com", 12345)]
    public void Various_realistic_payloads_parse(string line, string expectedUrl, int expectedPort)
    {
        var evt = Parser.TryParse(line, Ts).Should().BeOfType<ConnectionEvent>().Subject;
        evt.Url.Should().Be(expectedUrl);
        evt.Port.Should().Be(expectedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("LOADING LEVEL AreaSerbule")]
    [InlineData("loginCharacter, numChars=2")] // EVENT(Ok): loginCharacter body — not our event
    [InlineData("playing")]
    [InlineData("disconnected, url=s4.projectgorgon.com, port=9002")] // different verb
    [InlineData("connected")] // missing fields entirely (no comma after verb)
    [InlineData("connected,")] // empty body
    [InlineData("connected, port=9002")] // missing url
    [InlineData("connected, url=s4.projectgorgon.com")] // missing port
    [InlineData("connected, url=, port=9002")] // empty url
    [InlineData("connected, url=s4.projectgorgon.com, port=nope")] // non-numeric port
    [InlineData("connected, url=s4.projectgorgon.com, port=-1")] // out-of-range port
    [InlineData("connected, url=s4.projectgorgon.com, port=99999")] // out-of-range port
    public void Returns_null_for_unrelated_or_malformed_lines(string line)
    {
        Parser.TryParse(line, Ts).Should().BeNull();
    }

    [Fact]
    public void Unknown_extra_fields_are_tolerated_so_future_PG_additions_dont_break_parser()
    {
        // A future PG patch could add a "region=NA" or similar field. As
        // long as url+port are still present, the parser must succeed; the
        // unknown field is ignored.
        var evt = Parser.TryParse(
            "connected, url=s4.projectgorgon.com, port=9002, region=NA",
            Ts).Should().BeOfType<ConnectionEvent>().Subject;
        evt.Url.Should().Be("s4.projectgorgon.com");
        evt.Port.Should().Be(9002);
    }

    [Fact]
    public void Whitespace_around_equals_is_tolerated()
    {
        // PG emits "key=value" tightly today, but the parser shouldn't
        // choke on cosmetic whitespace if the grammar drifts a touch.
        var evt = Parser.TryParse(
            "connected, url = s4.projectgorgon.com , port = 9002",
            Ts).Should().BeOfType<ConnectionEvent>().Subject;
        evt.Url.Should().Be("s4.projectgorgon.com");
        evt.Port.Should().Be(9002);
    }

    [Fact]
    public void Timestamp_is_passed_through()
    {
        var custom = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var evt = Parser.TryParse(RealStrippedPayload, custom)
            .Should().BeOfType<ConnectionEvent>().Subject;
        evt.Timestamp.Should().Be(custom);
    }
}
