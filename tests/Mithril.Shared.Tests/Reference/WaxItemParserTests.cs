using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class WaxItemParserTests
{
    [Fact]
    public void WellFormedEntry_RendersEffectLines()
    {
        var refData = Phase7Fixture.Build(
            powers: [Phase7Fixture.Power("SharpSwordAccuracy", "Sword", suffix: "Sharpened Sword Accuracy",
                Phase7Fixture.Tier(4, "{BOOST_SKILL_SWORD_ACCURACY}{12}"))],
            attributes: [Phase7Fixture.Attribute("BOOST_SKILL_SWORD_ACCURACY", "Sword Accuracy", iconId: 42)]);

        var previews = ResultEffectsParser.ParseWaxItems(
            ["CraftWaxItem(SwordWax_Accuracy_T4,SharpSwordAccuracy,4,1250)"], refData);

        previews.Should().ContainSingle();
        var preview = previews[0];
        preview.WaxItemTemplate.Should().Be("SwordWax_Accuracy_T4");
        preview.PowerInternalName.Should().Be("SharpSwordAccuracy");
        preview.Suffix.Should().Be("Sharpened Sword Accuracy");
        preview.Tier.Should().Be(4);
        preview.Durability.Should().Be(1250);
        preview.EffectLines.Should().ContainSingle()
            .Which.Should().Be(new EffectLine(42, "Sword Accuracy: 12"));
        preview.DisplayLine.Should().Be("Sharpened Sword Accuracy · Tier 4 · 1250 uses");
    }

    [Fact]
    public void UnresolvablePower_IsSkipped()
    {
        var refData = Phase7Fixture.Build();
        var previews = ResultEffectsParser.ParseWaxItems(
            ["CraftWaxItem(SwordWax_Accuracy_T4,UnknownPower,4,1250)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void MissingTierOnPower_IsSkipped()
    {
        var refData = Phase7Fixture.Build(
            powers: [Phase7Fixture.Power("SharpSwordAccuracy", "Sword", suffix: null,
                Phase7Fixture.Tier(1, "{BOOST_SKILL_SWORD_ACCURACY}{1}"))]);

        var previews = ResultEffectsParser.ParseWaxItems(
            ["CraftWaxItem(SwordWax_Accuracy_T4,SharpSwordAccuracy,4,1250)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void NonIntegerTierOrDurability_IsSkipped()
    {
        var refData = Phase7Fixture.Build(
            powers: [Phase7Fixture.Power("SharpSwordAccuracy", "Sword", suffix: null,
                Phase7Fixture.Tier(4, "{BOOST_SKILL_SWORD_ACCURACY}{12}"))]);

        var previews = ResultEffectsParser.ParseWaxItems(
            ["CraftWaxItem(W,SharpSwordAccuracy,four,1250)",
             "CraftWaxItem(W,SharpSwordAccuracy,4,many)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void TooFewArgs_IsSkipped()
    {
        var refData = Phase7Fixture.Build();
        var previews = ResultEffectsParser.ParseWaxItems(
            ["CraftWaxItem(W,P,1)", "CraftWaxItem()"], refData);

        previews.Should().BeEmpty();
    }
}
