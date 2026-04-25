using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class CraftingEnhanceParserTests
{
    public class EquipBonusTests
    {
        [Theory]
        [InlineData("BoostItemEquipAdvancementTable(ForetoldHammerDamage)", "ForetoldHammerDamage", "Foretold Hammer Damage")]
        [InlineData("BoostItemEquipAdvancementTable(ForetoldBowDamage)", "ForetoldBowDamage", "Foretold Bow Damage")]
        [InlineData("BoostItemEquipAdvancementTable(ForetoldRingDamage)", "ForetoldRingDamage", "Foretold Ring Damage")]
        public void TableIsHumanized(string raw, string expectedRaw, string expectedDisplay)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseEquipBonuses([raw], refData);

            previews.Should().ContainSingle();
            previews[0].AdvancementTable.Should().Be(expectedRaw);
            previews[0].DisplayName.Should().Be(expectedDisplay);
            previews[0].DisplayLine.Should().Be($"Equipping grants progress in {expectedDisplay}");
        }

        [Fact]
        public void EmptyArg_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseEquipBonuses(
                ["BoostItemEquipAdvancementTable()"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void NonBoostPrefix_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseEquipBonuses(
                ["AddItemTSysPower(Foo,1)", "GiveTeleportationXp"], refData);

            previews.Should().BeEmpty();
        }
    }

    public class ElementModTests
    {
        [Theory]
        [InlineData("CraftingEnhanceItemFireMod(0.02,20)", "Fire damage", "+2% (max 20)")]
        [InlineData("CraftingEnhanceItemColdMod(0.02,20)", "Cold damage", "+2% (max 20)")]
        [InlineData("CraftingEnhanceItemElectricityMod(0.02,20)", "Electricity damage", "+2% (max 20)")]
        [InlineData("CraftingEnhanceItemPsychicMod(0.05,30)", "Psychic damage", "+5% (max 30)")]
        [InlineData("CraftingEnhanceItemNatureMod(0.02,20)", "Nature damage", "+2% (max 20)")]
        [InlineData("CraftingEnhanceItemDarknessMod(0.02,20)", "Darkness damage", "+2% (max 20)")]
        public void ScalarRendersAsPercentage(string raw, string expectedProperty, string expectedDetail)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseCraftingEnhancements([raw], refData);

            previews.Should().ContainSingle();
            previews[0].Property.Should().Be(expectedProperty);
            previews[0].Detail.Should().Be(expectedDetail);
        }
    }

    public class FlatBonusTests
    {
        [Theory]
        [InlineData("CraftingEnhanceItemArmor(3,5)", "Armor", "+3 (stack to 5)")]
        [InlineData("CraftingEnhanceItemArmor(5,5)", "Armor", "+5 (stack to 5)")]
        [InlineData("CraftingEnhanceItemPockets(2,12)", "Pockets", "+2 (stack to 12)")]
        [InlineData("CraftingEnhanceItemPockets(1,12)", "Pockets", "+1 (stack to 12)")]
        public void NAndStackCapAreParsed(string raw, string expectedProperty, string expectedDetail)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseCraftingEnhancements([raw], refData);

            previews.Should().ContainSingle();
            previews[0].Property.Should().Be(expectedProperty);
            previews[0].Detail.Should().Be(expectedDetail);
        }
    }

    public class RepairDurabilityTests
    {
        [Theory]
        [InlineData("RepairItemDurability(0.3,0.6,94,0,30)", "items at level 94")]
        [InlineData("RepairItemDurability(1,2,164,31,60)", "items at level 164")]
        [InlineData("RepairItemDurability(1,2,-1,0,0)", "any level")]
        public void LevelIsLifted(string raw, string expectedDetail)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseCraftingEnhancements([raw], refData);

            previews.Should().ContainSingle();
            previews[0].Property.Should().Be("Repairs item durability");
            previews[0].Detail.Should().Be(expectedDetail);
        }

        [Fact]
        public void TooFewArgs_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseCraftingEnhancements(
                ["RepairItemDurability(0.3,0.6)"], refData);

            previews.Should().BeEmpty();
        }
    }

    public class ZeroArgTagTests
    {
        [Fact]
        public void CraftingResetItem_IsEmitted()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseCraftingEnhancements(["CraftingResetItem"], refData);

            previews.Should().ContainSingle();
            previews[0].Property.Should().Be("Resets item to stock shape");
            previews[0].Detail.Should().BeNull();
            previews[0].DisplayLine.Should().Be("Resets item to stock shape");
        }

        [Fact]
        public void TransmogItemAppearance_IsEmitted()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseCraftingEnhancements(["TransmogItemAppearance"], refData);

            previews.Should().ContainSingle();
            previews[0].Property.Should().Be("Applies glamour to item");
        }
    }

    public class NegativeTests
    {
        [Fact]
        public void UnrelatedPrefix_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseCraftingEnhancements(
                ["BrewItem(1,0,Foo=Bar)", "ResearchFireMagic25", "DispelCalligraphyA"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void NullOrEmpty_ReturnsEmpty()
        {
            var refData = Phase7Fixture.Build();

            ResultEffectsParser.ParseCraftingEnhancements(null, refData).Should().BeEmpty();
            ResultEffectsParser.ParseCraftingEnhancements([], refData).Should().BeEmpty();
            ResultEffectsParser.ParseEquipBonuses(null, refData).Should().BeEmpty();
            ResultEffectsParser.ParseEquipBonuses([], refData).Should().BeEmpty();
        }
    }
}
