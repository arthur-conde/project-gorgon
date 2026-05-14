using FluentAssertions;
using Silmarillion.Navigation;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class ItemKeywordQueryMapperTests
{
    [Fact]
    public void Singleton_bare_tag_maps_to_keywords_contains()
    {
        var success = ItemKeywordQueryMapper.TryBuildQuery(["Crystal"], out var query);

        success.Should().BeTrue();
        query.Should().Be("Keywords CONTAINS \"Crystal\"");
    }

    [Fact]
    public void Singleton_EquipmentSlot_token_maps_to_EquipSlot_equality()
    {
        // EquipmentSlot:X is a synthesized matcher key (see ItemKeywordSynthesis.Enrich),
        // but Item.EquipSlot is a direct string property — so the chip click can produce
        // a clean equality query against the existing schema.
        var success = ItemKeywordQueryMapper.TryBuildQuery(["EquipmentSlot:MainHand"], out var query);

        success.Should().BeTrue();
        query.Should().Be("EquipSlot = \"MainHand\"");
    }

    [Fact]
    public void Composite_of_mappable_tokens_AND_joins_fragments()
    {
        var success = ItemKeywordQueryMapper.TryBuildQuery(
            ["EquipmentSlot:MainHand", "Crystal"],
            out var query);

        success.Should().BeTrue();
        query.Should().Be("EquipSlot = \"MainHand\" AND Keywords CONTAINS \"Crystal\"");
    }

    [Fact]
    public void Unmappable_token_fails_the_whole_slot()
    {
        // All-or-nothing: if any key in the slot can't be translated to a query clause,
        // the chip stays non-navigable rather than emit a lossy filter. MinTSysPrereq:N
        // has no item-side analogue today, so this real catalog pattern stays inert.
        var success = ItemKeywordQueryMapper.TryBuildQuery(
            ["EquipmentSlot:MainHand", "MinTSysPrereq:0"],
            out var query);

        success.Should().BeFalse();
        query.Should().BeNull();
    }

    [Theory]
    [InlineData("MinTSysPrereq:0")]
    [InlineData("MaxTSysPrereq:60")]
    [InlineData("SkillPrereq:Archery")]
    [InlineData("MinValue:1000")]
    [InlineData("MinRarity:Rare")]
    public void Known_unmappable_prefixes_fail(string token)
    {
        var success = ItemKeywordQueryMapper.TryBuildQuery([token], out var query);

        success.Should().BeFalse();
        query.Should().BeNull();
    }

    [Fact]
    public void Empty_slot_fails()
    {
        // Defensive: a slot with no keys can't produce a meaningful filter.
        var success = ItemKeywordQueryMapper.TryBuildQuery([], out var query);

        success.Should().BeFalse();
        query.Should().BeNull();
    }
}
