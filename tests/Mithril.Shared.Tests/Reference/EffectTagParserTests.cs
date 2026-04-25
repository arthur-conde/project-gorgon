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
}
