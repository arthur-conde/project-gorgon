using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class UnpreviewableExtractionParserTests
{
    [Fact]
    public void Extract_ResolvesCubeNameAndIcon()
    {
        var refData = Phase7Fixture.Build(
            items: [Phase7Fixture.Item(1, "MainHandAugment", "Main-Hand Augment")]);

        var previews = ResultEffectsParser.ParseUnpreviewableExtractions(
            ["ExtractTSysPower(MainHandAugment,WeaponAugmentBrewing,0,30)"], refData);

        previews.Should().ContainSingle();
        previews[0].SourceItemInternalName.Should().Be("MainHandAugment");
        previews[0].SourceItemDisplayName.Should().Be("Main-Hand Augment");
        previews[0].MinTier.Should().Be(0);
        previews[0].MaxTier.Should().Be(30);
        previews[0].DisplayLine.Should().Contain("Main-Hand Augment");
        previews[0].DisplayLine.Should().Contain("Level 0–30");
        previews[0].DisplayLine.Should().Contain("preview not available");
    }

    [Fact]
    public void Extract_FallsBackToHumanizedNameWhenCubeNotInItems()
    {
        // The cube item lookup should fail gracefully — chip still renders with
        // a humanised name. This is the path the bundled MainHandAugment cube
        // would take if it were absent from items.json (defensive only).
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseUnpreviewableExtractions(
            ["ExtractTSysPower(WeirdCube,SomeSkill,10,40)"], refData);

        previews.Should().ContainSingle();
        previews[0].SourceItemInternalName.Should().Be("WeirdCube");
        previews[0].SourceItemDisplayName.Should().Be("Weird Cube");
        previews[0].IconId.Should().BeNull();
        previews[0].MinTier.Should().Be(10);
        previews[0].MaxTier.Should().Be(40);
    }

    [Theory]
    [InlineData("ExtractTSysPower(ChestAugment,ArmorAugmentBrewing,31,60)", 31, 60)]
    [InlineData("ExtractTSysPower(NecklaceAugment,JewelryAugmentBrewing,91,120)", 91, 120)]
    public void Extract_LiftsTierBracket(string raw, int expectedMin, int expectedMax)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseUnpreviewableExtractions([raw], refData);

        previews.Should().ContainSingle();
        previews[0].MinTier.Should().Be(expectedMin);
        previews[0].MaxTier.Should().Be(expectedMax);
    }

    [Fact]
    public void Extract_WithFewerThan4Args_IsSkipped()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseUnpreviewableExtractions(
            ["ExtractTSysPower(MainHandAugment,SomeSkill,0)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void Extract_WithNonIntegerTiers_IsSkipped()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseUnpreviewableExtractions(
            ["ExtractTSysPower(MainHandAugment,SomeSkill,low,high)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void NonExtractPrefix_IsSkipped()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseUnpreviewableExtractions(
            ["TSysCraftedEquipment(Foo)", "AddItemTSysPower(Bar,1)", "BrewItem(1,0,X=Y)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void NullOrEmpty_ReturnsEmpty()
    {
        var refData = Phase7Fixture.Build();

        ResultEffectsParser.ParseUnpreviewableExtractions(null, refData).Should().BeEmpty();
        ResultEffectsParser.ParseUnpreviewableExtractions([], refData).Should().BeEmpty();
    }
}
