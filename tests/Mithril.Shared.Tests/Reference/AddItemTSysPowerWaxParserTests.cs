using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class AddItemTSysPowerWaxParserTests
{
    [Fact]
    public void WellFormedEntry_RendersEffectLines()
    {
        var refData = Phase7Fixture.Build(
            powers: [Phase7Fixture.Power("WaxArmor", "Armor", suffix: "of Armor",
                tiers: Phase7Fixture.Tier(4, "{MAX_ARMOR}{20}"))],
            attributes: [Phase7Fixture.Attribute("MAX_ARMOR", "Max Armor", iconId: 101)]);

        var previews = ResultEffectsParser.ParseAddItemTSysPowerWaxes(
            ["AddItemTSysPowerWax(WaxArmor,4,1250)"], refData);

        previews.Should().ContainSingle();
        var preview = previews[0];
        preview.PowerInternalName.Should().Be("WaxArmor");
        preview.Suffix.Should().Be("of Armor");
        preview.Tier.Should().Be(4);
        preview.Durability.Should().Be(1250);
        preview.EffectLines.Should().ContainSingle()
            .Which.Should().Be(new EffectLine(101, "Max Armor: 20"));
        preview.DisplayLine.Should().Be("of Armor · Tier 4 · 1250 uses");
    }

    [Fact]
    public void DisplayLine_FallsBackToInternalName_WhenSuffixMissing()
    {
        var refData = Phase7Fixture.Build(
            powers: [Phase7Fixture.Power("WaxAcid", "Acid", suffix: null,
                tiers: Phase7Fixture.Tier(3, "{MAX_ARMOR}{10}"))],
            attributes: [Phase7Fixture.Attribute("MAX_ARMOR", "Max Armor", iconId: 101)]);

        var previews = ResultEffectsParser.ParseAddItemTSysPowerWaxes(
            ["AddItemTSysPowerWax(WaxAcid,3,1250)"], refData);

        previews.Should().ContainSingle()
            .Which.DisplayLine.Should().Be("WaxAcid · Tier 3 · 1250 uses");
    }

    [Fact]
    public void UnresolvablePower_IsSkipped()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseAddItemTSysPowerWaxes(
            ["AddItemTSysPowerWax(UnknownPower,4,1250)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void MissingTierOnPower_IsSkipped()
    {
        var refData = Phase7Fixture.Build(
            powers: [Phase7Fixture.Power("WaxArmor", "Armor", suffix: null,
                tiers: Phase7Fixture.Tier(1, "{MAX_ARMOR}{1}"))]);

        var previews = ResultEffectsParser.ParseAddItemTSysPowerWaxes(
            ["AddItemTSysPowerWax(WaxArmor,99,1250)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void NonIntegerTierOrDurability_IsSkipped()
    {
        var refData = Phase7Fixture.Build(
            powers: [Phase7Fixture.Power("WaxArmor", "Armor", suffix: null,
                tiers: Phase7Fixture.Tier(4, "{MAX_ARMOR}{20}"))]);

        var previews = ResultEffectsParser.ParseAddItemTSysPowerWaxes(
            ["AddItemTSysPowerWax(WaxArmor,four,1250)",
             "AddItemTSysPowerWax(WaxArmor,4,many)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void TooFewArgs_IsSkipped()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseAddItemTSysPowerWaxes(
            ["AddItemTSysPowerWax(WaxArmor,4)", "AddItemTSysPowerWax()"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void MixedEffects_OnlyAddItemTSysPowerWaxIsReturned()
    {
        var refData = Phase7Fixture.Build(
            powers: [
                Phase7Fixture.Power("WaxArmor", "Armor", suffix: "of Armor",
                    tiers: Phase7Fixture.Tier(4, "{MAX_ARMOR}{20}")),
                Phase7Fixture.Power("SharpSwordAccuracy", "Sword", suffix: "Sharpened",
                    tiers: Phase7Fixture.Tier(2, "{MAX_ARMOR}{5}"))
            ],
            attributes: [Phase7Fixture.Attribute("MAX_ARMOR", "Max Armor", iconId: 101)]);

        string[] effects =
        [
            "AddItemTSysPowerWax(WaxArmor,4,1250)",
            "CraftWaxItem(SwordWax,SharpSwordAccuracy,2,500)",
            "AddItemTSysPower(WaxArmor,4)",
        ];

        var previews = ResultEffectsParser.ParseAddItemTSysPowerWaxes(effects, refData);

        previews.Should().ContainSingle()
            .Which.PowerInternalName.Should().Be("WaxArmor");
    }

    [Fact]
    public void NullOrEmptyInput_ReturnsEmpty()
    {
        var refData = Phase7Fixture.Build();
        ResultEffectsParser.ParseAddItemTSysPowerWaxes(null, refData).Should().BeEmpty();
        ResultEffectsParser.ParseAddItemTSysPowerWaxes([], refData).Should().BeEmpty();
    }
}
