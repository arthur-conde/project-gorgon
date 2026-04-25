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
    public void TypedHandlerPrefix_IsExcludedFromFallback()
    {
        // Prefixes that have their own typed parser (ParseAugments,
        // ParseTaughtRecipes, ParseEquipBonuses, etc.) must NOT also emit a
        // humanised EffectTagPreview, otherwise the same recipe shows duplicate
        // text — once as a structured chip, once as a fallback line.
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(
            [
                "AddItemTSysPower(Foo,1)",
                "BestowRecipeIfNotKnown(Bar)",
                "BoostItemEquipAdvancementTable(Baz)",
                "BrewItem(1,0,X=Y)",
                "ResearchFireMagic25",
                "GiveTeleportationXp",
                "CreateMiningSurvey1X(SomeName)",
                "CreateEltibuleTreasureMapPoor",
            ], refData);

        previews.Should().BeEmpty();
    }

    [Theory]
    [InlineData("SomeUnrelatedEffect", "Some Unrelated Effect")]
    [InlineData("DoSomething(arg)", "Do Something")]
    [InlineData("FrobnicateWidget123", "Frobnicate Widget 123")]
    public void GenericFallback_HumanizesUnknownIdentifiers(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("particle_psychic")]
    [InlineData("lowercaseStart")]
    [InlineData("123Numeric")]
    public void GenericFallback_RejectsNonIdentifierShape(string raw)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Particle_Psychic")]
    [InlineData("Particle_Fire")]
    public void Particle_IsSilentAllowList(string raw)
    {
        // Particle_* tags carry no player-meaningful semantics — recognise the
        // shape and suppress; do not let the generic fallback humanise them.
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

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
    public void DecomposeWithEmptySlot_FallsThroughToGenericHumanizer()
    {
        // Defensive case — prefix + suffix with nothing in between, not seen in
        // real data. Both the augment-slot handler (slot empty) and the non-augment
        // handler (substance == "AugmentResources" is the explicit reject) bail,
        // so the input flows through the generic identifier-shape fallback and
        // shows up as a humanised line. That's the correct post-Step-8 behaviour.
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags(
            ["DecomposeItemIntoAugmentResources"], refData);

        previews.Should().ContainSingle()
            .Which.DisplayText.Should().Be("Decompose Item Into Augment Resources");
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

    [Theory]
    [InlineData("DecomposeItemIntoPhlogiston", "Decomposes item into phlogiston")]
    [InlineData("DecomposeItemIntoCrystalIce", "Decomposes item into crystal ice")]
    [InlineData("DecomposeItemIntoFairyDust", "Decomposes item into fairy dust")]
    [InlineData("DecomposeDemonOreIntoEssence", "Decomposes demon ore into essence")]
    public void NonAugmentDecomposeVariants_AreEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("SaveCurrentMushroomCircle", "Saves current mushroom circle")]
    [InlineData("TeleportToLastUsedMushroomCircle", "Teleports to last-used mushroom circle")]
    [InlineData("TeleportToBoundMushroomCircle3", "Teleports to bound mushroom circle 3")]
    [InlineData("BindToMushroomCircle1", "Binds to mushroom circle 1")]
    [InlineData("TeleportToBoundTeleportCircle2", "Teleports to bound teleport circle 2")]
    [InlineData("BindToTeleportCircle4", "Binds to teleport circle 4")]
    [InlineData("SpawnPlayerPortal1", "Spawns player portal 1")]
    [InlineData("StoragePortal2", "Storage portal 2")]
    [InlineData("TeleportToGuildHall", "Teleports to guild hall")]
    public void MushroomTeleportPortalFamilies_AreEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("HelpMsg_Baking", "Help: Baking")]
    [InlineData("HelpMsg_FireMagic", "Help: Fire Magic")]
    public void HelpMsg_IsHumanized(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("StorageCrateDruid12Items", "Druid storage crate (12 items)")]
    [InlineData("StorageCrateDruid20Items", "Druid storage crate (20 items)")]
    public void StorageCrateDruidNItems_AreEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("Teleport(AreaSerbule, Landing_Boat)", "Teleports to Landing_ Boat in Serbule")]
    [InlineData("Teleport(Povus, MainBank)", "Teleports to Main Bank in Povus")]
    public void Teleport_ParametrisedIsEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("DeltaCurFairyEnergy(-10)", "Reduces fairy energy by 10")]
    [InlineData("DeltaCurFairyEnergy(20)", "Adds fairy energy by 20")]
    public void DeltaCurFairyEnergy_LiftsSignedDelta(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("ConsumeItemUses(SomeTemplate, 3)", "Consumes 3 use(s) of Some Template")]
    public void ConsumeItemUses_LiftsTemplateAndCount(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }

    [Theory]
    [InlineData("CraftingDyeItem", "Dyes the item")]
    [InlineData("HoplologyStudy", "Hoplology study")]
    [InlineData("MoonPhaseCheck", "Checks moon phase")]
    [InlineData("WeatherReport", "Reports current weather")]
    [InlineData("PolymorphRabbitPermanentBlue", "Polymorphs to a permanent blue rabbit")]
    public void OneOffBehaviouralTags_AreEmitted(string raw, string expected)
    {
        var refData = Phase7Fixture.Build();

        var previews = ResultEffectsParser.ParseEffectTags([raw], refData);

        previews.Should().ContainSingle().Which.DisplayText.Should().Be(expected);
    }
}
