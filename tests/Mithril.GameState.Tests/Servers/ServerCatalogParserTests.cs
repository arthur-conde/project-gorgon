using FluentAssertions;
using Mithril.GameState.Servers;
using Mithril.GameState.Servers.Parsing;
using Xunit;

namespace Mithril.GameState.Tests.Servers;

public sealed class ServerCatalogParserTests
{
    private static readonly ServerCatalogParser Parser = new();
    private static readonly DateTime Ts = new(2026, 5, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Real Player.log payload (this machine, 2026-05-21) — five PG worlds
    /// in PG's actual emission order (s4..s0). Envelope-stripped per the
    /// L0.5 classifier (SystemSignalKind.Servers eats the "Servers: "
    /// prefix). Kept verbatim so a PG patch that changes the shape shows
    /// up as a test diff.
    /// </summary>
    private const string RealStrippedPayload =
        """[ { "AllowGuests" : true, "Port" : 9002, "Description" : "<b>Laeth - \"Time and Space\"</b>\nA brand new world! Help forge a new community that can withstand the onslaught of the coming demon army.", "Url" : "s4.projectgorgon.com", "ID" : "s4", "Name" : "Laeth" }, { "AllowGuests" : true, "Port" : 9002, "Description" : "<b>Miraverre - \"Dreams\"</b>\nA brand new world! Help forge a new community that can withstand the onslaught of the coming demon army.", "Url" : "s3.projectgorgon.com", "ID" : "s3", "Name" : "Miraverre" }, { "AllowGuests" : true, "Port" : 9002, "Description" : "<b>Strekios - \"Self Improvement\"</b>\nA brand new world! Help forge a new community that can withstand the onslaught of the coming demon army.", "Url" : "s2.projectgorgon.com", "ID" : "s2", "Name" : "Strekios" }, { "AllowGuests" : true, "Port" : 9002, "Description" : "<b>Dreva - \"Balance\"</b>\nAn almost-brand-new world! Help forge a new community that can withstand the onslaught of the coming demon army.", "Url" : "s1.projectgorgon.com", "ID" : "s1", "Name" : "Dreva" }, { "AllowGuests" : true, "Port" : 9002, "Description" : "<b>Arisetsu - \"Hope and Warmth\"</b>\nPlay on the original server from pre-launch. With more unlocked resources and a friendly established community, this is the easiest world for new players to start on.", "Url" : "s0.projectgorgon.com", "ID" : "s0", "Name" : "Arisetsu" } ]""";

    [Fact]
    public void Real_payload_parses_all_five_servers_in_emission_order()
    {
        var evt = Parser.TryParse(RealStrippedPayload, Ts)
            .Should().BeOfType<ServerCatalogEvent>().Subject;

        evt.Timestamp.Should().Be(Ts);
        evt.Entries.Should().HaveCount(5);

        var entries = evt.Entries.ToArray();
        entries[0].Should().BeEquivalentTo(new ServerEntry(
            "s4", "Laeth", "s4.projectgorgon.com", 9002,
            "<b>Laeth - \"Time and Space\"</b>\nA brand new world! Help forge a new community that can withstand the onslaught of the coming demon army."));
        entries[1].Id.Should().Be("s3");
        entries[1].Name.Should().Be("Miraverre");
        entries[2].Id.Should().Be("s2");
        entries[2].Name.Should().Be("Strekios");
        entries[3].Id.Should().Be("s1");
        entries[3].Name.Should().Be("Dreva");
        entries[4].Should().BeEquivalentTo(new ServerEntry(
            "s0", "Arisetsu", "s0.projectgorgon.com", 9002,
            "<b>Arisetsu - \"Hope and Warmth\"</b>\nPlay on the original server from pre-launch. With more unlocked resources and a friendly established community, this is the easiest world for new players to start on."));
    }

    [Fact]
    public void Description_bbcode_and_embedded_newlines_are_preserved_verbatim()
    {
        var evt = Parser.TryParse(RealStrippedPayload, Ts)
            .Should().BeOfType<ServerCatalogEvent>().Subject;

        // The JSON decoder unescapes `\"` and `\n`; the surviving Description
        // string should contain the literal characters PG intended.
        evt.Entries.First(e => e.Id == "s4").Description.Should().Contain("\"Time and Space\"");
        evt.Entries.First(e => e.Id == "s4").Description.Should().Contain("\n"); // real newline
    }

    [Fact]
    public void Raw_prefixed_form_is_accepted_for_ad_hoc_callers()
    {
        // Defensive — production callers pass the L0.5-stripped form, but a
        // test or REPL caller may hand the full "Servers: [ … ]" line.
        var raw = "Servers: " + RealStrippedPayload;
        var evt = Parser.TryParse(raw, Ts)
            .Should().BeOfType<ServerCatalogEvent>().Subject;
        evt.Entries.Should().HaveCount(5);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("LOADING LEVEL AreaSerbule")]
    [InlineData("Servers: not-an-array")]
    [InlineData("[ { \"Url\": \"s9.projectgorgon.com\" } ]")] // missing required ID/Name/Port
    [InlineData("[ ")] // truncated JSON
    [InlineData("[ { \"ID\": \"sX\", \"Name\": \"X\", \"Url\": \"s.foo\", \"Port\": \"nope\" } ]")] // non-numeric port
    public void Returns_null_for_unrelated_or_malformed_lines(string line)
    {
        Parser.TryParse(line, Ts).Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_any_entry_is_invalid_so_partial_catalogs_never_publish()
    {
        // First entry is fine, second is missing Url — entire event should be
        // dropped rather than publish a partial catalog.
        const string partiallyBad =
            """[ { "ID": "s0", "Name": "Arisetsu", "Url": "s0.projectgorgon.com", "Port": 9002, "Description": "..." }, { "ID": "s1", "Name": "Dreva", "Port": 9002, "Description": "..." } ]""";
        Parser.TryParse(partiallyBad, Ts).Should().BeNull();
    }

    [Fact]
    public void Description_is_optional_to_survive_PG_dropping_the_field()
    {
        const string descLess =
            """[ { "ID": "s0", "Name": "Arisetsu", "Url": "s0.projectgorgon.com", "Port": 9002 } ]""";
        var evt = Parser.TryParse(descLess, Ts)
            .Should().BeOfType<ServerCatalogEvent>().Subject;

        evt.Entries.Should().ContainSingle()
            .Which.Description.Should().BeEmpty();
    }

    [Fact]
    public void Empty_array_parses_to_empty_catalog()
    {
        // PG never emits this in practice but the parser shouldn't choke.
        var evt = Parser.TryParse("[]", Ts)
            .Should().BeOfType<ServerCatalogEvent>().Subject;
        evt.Entries.Should().BeEmpty();
    }
}
