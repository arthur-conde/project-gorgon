using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class RecipeCooldownParserTests
{
    [Theory]
    [InlineData("AdjustRecipeReuseTime(-86400,QuarterMoon)", -86400, "Quarter Moon", "Reduces cooldown by 1d on Quarter Moon")]
    [InlineData("AdjustRecipeReuseTime(-3600,FullMoon)", -3600, "Full Moon", "Reduces cooldown by 1h on Full Moon")]
    [InlineData("AdjustRecipeReuseTime(3600,Daytime)", 3600, "Daytime", "Adds cooldown by 1h on Daytime")]
    [InlineData("AdjustRecipeReuseTime(-90)", -90, null, "Reduces cooldown by 1m 30s")]
    [InlineData("AdjustRecipeReuseTime(-7200)", -7200, null, "Reduces cooldown by 2h")]
    public void DeltaAndConditionAreParsed(string raw, int expectedDelta, string? expectedCondition, string expectedDisplay)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseRecipeCooldowns([raw], refData);

        previews.Should().ContainSingle();
        previews[0].DeltaSeconds.Should().Be(expectedDelta);
        previews[0].Condition.Should().Be(expectedCondition);
        previews[0].DisplayText.Should().Be(expectedDisplay);
    }

    [Fact]
    public void NonIntegerDelta_IsSkipped()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseRecipeCooldowns(
            ["AdjustRecipeReuseTime(notAnInt,Foo)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void MultiUnitDuration_RendersAllParts()
    {
        // 86400 + 3600 + 60 + 5 = 90065 seconds → "1d 1h 1m 5s"
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseRecipeCooldowns(
            ["AdjustRecipeReuseTime(-90065,SomeMoon)"], refData);

        previews.Should().ContainSingle();
        previews[0].DisplayText.Should().Be("Reduces cooldown by 1d 1h 1m 5s on Some Moon");
    }

    [Fact]
    public void NonAdjustPrefix_IsSkipped()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseRecipeCooldowns(
            ["AddItemTSysPower(Foo,1)", "BrewItem(1,0,X=Y)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void NullOrEmpty_ReturnsEmpty()
    {
        var refData = Phase7Fixture.Build();

        ResultEffectsParser.ParseRecipeCooldowns(null, refData).Should().BeEmpty();
        ResultEffectsParser.ParseRecipeCooldowns([], refData).Should().BeEmpty();
    }
}
