using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.Reference.Models.Npcs;
using Xunit;

namespace Mithril.Reference.Tests;

/// <summary>
/// Locks the single-source-of-truth cap-increase parser (#350). The key contract:
/// <see cref="StoreCapIncreaseParser.Parse"/> keeps structurally-valid rows even when
/// the gold segment is non-numeric (<c>GoldCap == null</c>), while
/// <see cref="StoreCapIncreaseParser.ParseRequiringGold"/> drops those — byte-identical
/// to the pre-unification Smaug projection (<c>ReferenceDataService.ParseCapIncreases</c>).
/// </summary>
public class StoreCapIncreaseParserTests
{
    [Fact]
    public void ParseLine_full_three_part_row()
    {
        var cap = StoreCapIncreaseParser.ParseLine("Despised:5000:Armor,Weapon,CorpseTrophy");

        cap.Should().NotBeNull();
        cap!.Tier.Should().Be("Despised");
        cap.GoldCap.Should().Be(5000);
        cap.Keywords.Should().Equal("Armor", "Weapon", "CorpseTrophy");
    }

    [Fact]
    public void ParseLine_two_part_row_has_empty_keywords()
    {
        var cap = StoreCapIncreaseParser.ParseLine("Friends:1000");

        cap.Should().NotBeNull();
        cap!.Tier.Should().Be("Friends");
        cap.GoldCap.Should().Be(1000);
        cap.Keywords.Should().BeEmpty();
    }

    [Fact]
    public void ParseLine_empty_third_segment_yields_empty_keywords()
    {
        var cap = StoreCapIncreaseParser.ParseLine("Friends:1000:");

        cap!.Keywords.Should().BeEmpty();
        cap.GoldCap.Should().Be(1000);
    }

    [Fact]
    public void ParseLine_trims_and_drops_blank_keywords()
    {
        var cap = StoreCapIncreaseParser.ParseLine("Neutral:50: Armor , , Weapon ");

        cap!.Keywords.Should().Equal("Armor", "Weapon");
    }

    [Fact]
    public void ParseLine_non_integer_gold_keeps_row_with_null_goldcap()
    {
        var cap = StoreCapIncreaseParser.ParseLine("Friends:notanumber:Armor");

        cap.Should().NotBeNull();
        cap!.Tier.Should().Be("Friends");
        cap.GoldCap.Should().BeNull();
        cap.Keywords.Should().Equal("Armor");
    }

    [Theory]
    [InlineData("Friends")]      // < 2 colon parts
    [InlineData("")]             // empty
    [InlineData("   ")]          // whitespace
    [InlineData(null)]           // null
    public void ParseLine_structurally_unusable_returns_null(string? raw)
    {
        StoreCapIncreaseParser.ParseLine(raw).Should().BeNull();
    }

    [Fact]
    public void Parse_keeps_bad_gold_rows()
    {
        IReadOnlyList<string> raw =
            ["Despised:5000:Armor", "Friends:notanumber:Weapon", "single", "  "];

        var parsed = StoreCapIncreaseParser.Parse(raw);

        parsed.Select(c => c.Tier).Should().Equal("Despised", "Friends");
        parsed[1].GoldCap.Should().BeNull();
    }

    [Fact]
    public void ParseRequiringGold_drops_bad_gold_rows_matching_legacy_projection()
    {
        IReadOnlyList<string> raw =
            ["Despised:5000:Armor", "Friends:notanumber:Weapon", "single", "  "];

        var parsed = StoreCapIncreaseParser.ParseRequiringGold(raw);

        parsed.Should().ContainSingle();
        parsed[0].Tier.Should().Be("Despised");
        parsed[0].GoldCap.Should().Be(5000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_null_or_empty_input_yields_empty(bool requireGold)
    {
        var fromNull = requireGold
            ? StoreCapIncreaseParser.ParseRequiringGold(null)
            : StoreCapIncreaseParser.Parse(null);
        var fromEmpty = requireGold
            ? StoreCapIncreaseParser.ParseRequiringGold([])
            : StoreCapIncreaseParser.Parse([]);

        fromNull.Should().BeEmpty();
        fromEmpty.Should().BeEmpty();
    }
}
