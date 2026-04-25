using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class ItemProducingParserTests
{
    public class BrewItemTests
    {
        [Theory]
        [InlineData("BrewItem(1,0,BrewingFruitA4=Partying4)", 1)]
        [InlineData("BrewItem(2,15,BrewingFruitA4+BrewingMushroomA4=Partying4+SkillSpecificPowerCosts12)", 2)]
        [InlineData("BrewItem(5,75,X+Y=Z+W)", 5)]
        public void TierIsLifted(string raw, int expectedTier)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing([raw], refData);

            previews.Should().ContainSingle();
            previews[0].DisplayName.Should().Be("Brewed item");
            previews[0].Qualifier.Should().Be($"Tier {expectedTier}");
            previews[0].DisplayLine.Should().Be($"Brewed item (Tier {expectedTier})");
            previews[0].IconId.Should().BeNull();
            previews[0].ResolvedItemInternalName.Should().BeNull();
        }

        [Fact]
        public void NonIntTier_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing(
                ["BrewItem(notAnInt,0,Foo=Bar)"], refData);

            previews.Should().BeEmpty();
        }
    }

    public class SummonPlantTests
    {
        [Fact]
        public void ResolvesItemNameWhenKnown()
        {
            var item = Phase7Fixture.Item(1, "NethBread", "Neth Bread");
            var refData = Phase7Fixture.Build(items: [item]);

            var previews = ResultEffectsParser.ParseItemProducing(
                ["SummonPlant(SummonedBakingBread,,NethBread~300~450~500~700~875)"], refData);

            previews.Should().ContainSingle();
            previews[0].DisplayName.Should().Be("Neth Bread");
            previews[0].ResolvedItemInternalName.Should().Be("NethBread");
            previews[0].Qualifier.Should().BeNull();
        }

        [Fact]
        public void HumanizesUnknownItemName()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing(
                ["SummonPlant(SummonedBakingBread,,UnknownItemName~100~200)"], refData);

            previews.Should().ContainSingle();
            previews[0].DisplayName.Should().Be("Unknown Item Name");
            previews[0].ResolvedItemInternalName.Should().Be("UnknownItemName");
            previews[0].IconId.Should().BeNull();
        }

        [Fact]
        public void EmptyThirdArg_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing(
                ["SummonPlant(Foo,,)"], refData);

            previews.Should().BeEmpty();
        }
    }

    public class MiningSurveyTests
    {
        [Theory]
        [InlineData("CreateMiningSurvey1X(MiningSurveyKurMountains1X)", "1X")]
        [InlineData("CreateMiningSurvey5(MiningSurveyEltibule5)", "5")]
        [InlineData("CreateMiningSurvey7Y(MiningSurveyFoo7Y)", "7Y")]
        public void TierAndItemAreParsed(string raw, string expectedTier)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing([raw], refData);

            previews.Should().ContainSingle();
            previews[0].Qualifier.Should().Be($"Mining Survey {expectedTier}");
        }

        [Fact]
        public void ResolvesItemFromRefData()
        {
            var item = Phase7Fixture.Item(2, "MiningSurveyEltibule5", "Eltibule Mining Survey 5");
            var refData = Phase7Fixture.Build(items: [item]);

            var previews = ResultEffectsParser.ParseItemProducing(
                ["CreateMiningSurvey5(MiningSurveyEltibule5)"], refData);

            previews.Should().ContainSingle();
            previews[0].DisplayName.Should().Be("Eltibule Mining Survey 5");
            previews[0].ResolvedItemInternalName.Should().Be("MiningSurveyEltibule5");
        }
    }

    public class GeologySurveyTests
    {
        [Theory]
        [InlineData("CreateGeologySurveyRedwall(GeologySurveySerbule0)", "Geology Survey · Redwall")]
        [InlineData("CreateGeologySurveyBlue(GeologySurveySerbule1)", "Geology Survey · Blue")]
        [InlineData("CreateGeologySurveyGreenPovus(GeologySurveyPovus2)", "Geology Survey · Green Povus")]
        public void ColorIsHumanized(string raw, string expectedQualifier)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing([raw], refData);

            previews.Should().ContainSingle();
            previews[0].Qualifier.Should().Be(expectedQualifier);
        }
    }

    public class TreasureMapTests
    {
        [Theory]
        [InlineData("CreateEltibuleTreasureMapPoor", "Eltibule", "Poor")]
        [InlineData("CreateIlmariTreasureMapGood", "Ilmari", "Good")]
        [InlineData("CreateSunValeTreasureMapAmazing", "Sun Vale", "Amazing")]
        public void RegionAndQualityAreParsed(string raw, string expectedRegion, string expectedQuality)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing([raw], refData);

            previews.Should().ContainSingle();
            previews[0].DisplayName.Should().Be($"{expectedRegion} Treasure Map");
            previews[0].Qualifier.Should().Be(expectedQuality);
        }

        [Fact]
        public void ResolvesItemViaTreasureMapPrefix()
        {
            var item = Phase7Fixture.Item(3, "TreasureMapEltibulePoor", "Treasure Map (Eltibule, Poor)");
            var refData = Phase7Fixture.Build(items: [item]);

            var previews = ResultEffectsParser.ParseItemProducing(
                ["CreateEltibuleTreasureMapPoor"], refData);

            previews.Should().ContainSingle();
            previews[0].DisplayName.Should().Be("Treasure Map (Eltibule, Poor)");
            previews[0].ResolvedItemInternalName.Should().Be("TreasureMapEltibulePoor");
        }
    }

    public class FixedTagTests
    {
        [Fact]
        public void CreateNecroFuel_IsEmitted()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing(["CreateNecroFuel"], refData);

            previews.Should().ContainSingle();
            previews[0].DisplayName.Should().Be("Necromancy Fuel");
            previews[0].DisplayLine.Should().Be("Necromancy Fuel");
        }

        [Fact]
        public void GiveNonMagicalLootProfile_IsEmitted()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing(
                ["GiveNonMagicalLootProfile(PiecesOfGlass)"], refData);

            previews.Should().ContainSingle();
            previews[0].DisplayName.Should().Be("Loot from Pieces Of Glass");
        }

        [Fact]
        public void EmptyProfileArg_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing(
                ["GiveNonMagicalLootProfile()"], refData);

            previews.Should().BeEmpty();
        }
    }

    public class NegativeTests
    {
        [Fact]
        public void UnrelatedPrefix_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseItemProducing(
                ["AddItemTSysPower(Foo,1)", "ResearchFireMagic25", "DispelCalligraphyA"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void NullOrEmpty_ReturnsEmpty()
        {
            var refData = Phase7Fixture.Build();

            ResultEffectsParser.ParseItemProducing(null, refData).Should().BeEmpty();
            ResultEffectsParser.ParseItemProducing([], refData).Should().BeEmpty();
        }
    }
}
