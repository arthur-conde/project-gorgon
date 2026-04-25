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

    [Theory]
    [InlineData("CalligraphyCombo1C", "Combo: Calligraphy Combo 1 (Slot C)")]
    [InlineData("CalligraphyCombo7C", "Combo: Calligraphy Combo 7 (Slot C)")]
    public void CalligraphyComboNN_LetterSuffix_IsEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("MeditationHealth5", "Meditation: Health Tier 5")]
    [InlineData("MeditationPower3", "Meditation: Power Tier 3")]
    [InlineData("MeditationBreath4", "Meditation: Breath Tier 4")]
    [InlineData("MeditationVulnPsi5", "Meditation: Psychic Vulnerability Tier 5")]
    [InlineData("MeditationVulnCold2", "Meditation: Cold Vulnerability Tier 2")]
    [InlineData("MeditationVulnFire1", "Meditation: Fire Vulnerability Tier 1")]
    [InlineData("MeditationVulnDarkness7", "Meditation: Darkness Vulnerability Tier 7")]
    [InlineData("MeditationVulnNature3", "Meditation: Nature Vulnerability Tier 3")]
    [InlineData("MeditationVulnElectricity6", "Meditation: Electricity Vulnerability Tier 6")]
    [InlineData("MeditationCritDmg4", "Meditation: Crit Damage Tier 4")]
    [InlineData("MeditationIndirect2", "Meditation: Indirect Damage Tier 2")]
    [InlineData("MeditationBuffIndirectCold3", "Meditation: Indirect Cold Buff Tier 3")]
    [InlineData("MeditationDeathAvoidance5", "Meditation: Death Avoidance Tier 5")]
    [InlineData("MeditationBodyHeat1", "Meditation: Body Heat Tier 1")]
    [InlineData("MeditationMetabolism2", "Meditation: Metabolism Tier 2")]
    public void MeditationTierFamilies_AreEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Fact]
    public void MeditationNoDaily_IsEmitted()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(["MeditationNoDaily"], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be("Meditation: No Daily");
    }

    [Theory]
    [InlineData("CalligraphySlash3", "Calligraphy: Slashing Tier 3")]
    [InlineData("CalligraphyFirstAid2", "Calligraphy: First Aid Tier 2")]
    [InlineData("CalligraphyRage5", "Calligraphy: Rage Tier 5")]
    [InlineData("CalligraphyArmorRepair1", "Calligraphy: Armor Repair Tier 1")]
    [InlineData("CalligraphyPiercing4", "Calligraphy: Piercing Tier 4")]
    [InlineData("CalligraphySlashingFlat2", "Calligraphy: Slashing Flat Tier 2")]
    public void CalligraphyTypedSubFamilies_AreEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("Calligraphy1", "Calligraphy 1")]
    [InlineData("Calligraphy15", "Calligraphy 15")]
    [InlineData("Calligraphy1B", "Calligraphy 1 Slot B")]
    [InlineData("Calligraphy5D", "Calligraphy 5 Slot D")]
    [InlineData("Calligraphy10D", "Calligraphy 10 Slot D")]
    public void CalligraphyNumberSlot_IsEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("Whittling3", "Whittling Tier 3")]
    [InlineData("WhittlingKnifeBuff5", "Whittling Knife Buff Tier 5")]
    public void WhittlingTierFamilies_AreEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Fact]
    public void Augury_IsEmittedAsTier()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(["Augury2"], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be("Augury Tier 2");
    }

    [Theory]
    [InlineData("SpawnPremonition_FireBolt", "Premonition: Fire Bolt")]
    [InlineData("SpawnPremonition_ColdShield", "Premonition: Cold Shield")]
    public void SpawnPremonition_IsHumanized(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Fact]
    public void DispelSpawnPremonitionsOnDeath_IsEmitted()
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(["DispelSpawnPremonitionsOnDeath"], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be("Dispels premonitions on death");
    }

    [Theory]
    [InlineData("Infertility", "Infertility")]
    [InlineData("SleepResistance", "Sleep Resistance")]
    [InlineData("SexualEnergy", "Sexual Energy")]
    [InlineData("ArgumentResistance", "Argument Resistance")]
    public void ZeroArgStatusTags_AreEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("PermanentlyRaiseMaxTempestEnergy(1)", "Permanently raises max Tempest Energy by 1")]
    [InlineData("PermanentlyRaiseMaxTempestEnergy(5)", "Permanently raises max Tempest Energy by 5")]
    public void PermanentlyRaiseMaxTempestEnergy_LiftsArg(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }
}
