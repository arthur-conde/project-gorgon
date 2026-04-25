using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class EffectTagParserTests
{
    [Fact]
    public void DispelCalligraphyVariants_AreEmitted()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(
            ["DispelCalligraphyA", "DispelCalligraphyB", "DispelCalligraphyC"], refData);

        previews.Select(p => p.DisplayText).Should().Equal(
            "Calligraphy Slot A", "Calligraphy Slot B", "Calligraphy Slot C");
    }

    [Fact]
    public void CalligraphyComboNN_IsEmittedAsHumanizedNumber()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(
            ["CalligraphyCombo01", "CalligraphyCombo7"], refData);

        previews.Should().HaveCount(2);
        previews[0].DisplayText.Should().Be("Combo: Calligraphy Combo 1");
        previews[1].DisplayText.Should().Be("Combo: Calligraphy Combo 7");
    }

    [Fact]
    public void MeditationWithDaily_NoArg_HasGenericLabel()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(
            ["MeditationWithDaily"], refData);

        previews.Should().ContainSingle()
            .Which.DisplayText.Should().Be("Grants: Daily Meditation Combo");
    }

    [Fact]
    public void MeditationWithDaily_WithCombo_HumanizesCamelCase()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(
            ["MeditationWithDaily(UnarmedMeditationCombo1)"], refData);

        previews.Should().ContainSingle()
            .Which.DisplayText.Should().Be("Grants: Daily Meditation Combo — Unarmed Meditation Combo 1");
    }

    [Fact]
    public void TrulyUnknownPrefix_IsNotEmitted()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(
            ["SomeUnrelatedEffect", "DoSomething(arg)", "AddItemTSysPower(Foo,1)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void ApplyAugmentOil_IsEmitted()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(["ApplyAugmentOil"], refData);

        previews.Should().ContainSingle()
            .Which.DisplayText.Should().Be("Applies augment oil");
    }

    [Fact]
    public void RemoveAddedTSysPowerFromItem_IsEmitted()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(["RemoveAddedTSysPowerFromItem"], refData);

        previews.Should().ContainSingle()
            .Which.DisplayText.Should().Be("Removes augment from item");
    }

    [Fact]
    public void ApplyAddItemTSysPowerWaxFromSourceItem_IsEmitted()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(
            ["ApplyAddItemTSysPowerWaxFromSourceItem"], refData);

        previews.Should().ContainSingle()
            .Which.DisplayText.Should().Be("Applies augment wax from source item");
    }

    [Theory]
    [InlineData("DecomposeMainHandItemIntoAugmentResources", "Decomposes equipped main hand into augment resources")]
    [InlineData("DecomposeOffHandItemIntoAugmentResources", "Decomposes equipped off hand into augment resources")]
    [InlineData("DecomposeHandsItemIntoAugmentResources", "Decomposes equipped hands into augment resources")]
    [InlineData("DecomposeChestItemIntoAugmentResources", "Decomposes equipped chest into augment resources")]
    [InlineData("DecomposeLegItemIntoAugmentResources", "Decomposes equipped leg into augment resources")]
    [InlineData("DecomposeHelmItemIntoAugmentResources", "Decomposes equipped helm into augment resources")]
    [InlineData("DecomposeFeetItemIntoAugmentResources", "Decomposes equipped feet into augment resources")]
    [InlineData("DecomposeRingItemIntoAugmentResources", "Decomposes equipped ring into augment resources")]
    [InlineData("DecomposeNecklaceItemIntoAugmentResources", "Decomposes equipped necklace into augment resources")]
    public void DecomposeSlotIntoAugmentResources_IsEmittedWithHumanizedSlot(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle()
            .Which.DisplayText.Should().Be(expected);
    }

    [Fact]
    public void DecomposeWithEmptySlot_IsNotEmitted()
    {
        var refData = Phase7Fixture.Build();

        // Prefix + suffix with nothing in between — defensive; not seen in real data.
        var previews = ResultEffectsParser.ParseEffectTags(
            ["DecomposeItemIntoAugmentResources"], refData);

        previews.Should().BeEmpty();
    }
}
