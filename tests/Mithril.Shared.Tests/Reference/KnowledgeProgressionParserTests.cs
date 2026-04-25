using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class KnowledgeProgressionParserTests
{
    public class ResearchProgressTests
    {
        [Theory]
        [InlineData("ResearchWeatherWitching1", "WeatherWitching", 1, "Research Weather Witching → Level 1")]
        [InlineData("ResearchFireMagic25", "FireMagic", 25, "Research Fire Magic → Level 25")]
        [InlineData("ResearchIceMagic50", "IceMagic", 50, "Research Ice Magic → Level 50")]
        [InlineData("ResearchExoticFireWalls105", "ExoticFireWalls", 105, "Research Exotic Fire Walls → Level 105")]
        public void TopicAndLevelAreParsed(string raw, string expectedTopic, int expectedLevel, string expectedLine)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseResearchProgress([raw], refData);

            previews.Should().ContainSingle();
            previews[0].Topic.Should().Be(expectedTopic);
            previews[0].Level.Should().Be(expectedLevel);
            previews[0].DisplayLine.Should().Be(expectedLine);
        }

        [Fact]
        public void ResearchWithoutTrailingDigits_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseResearchProgress(
                ["ResearchFireMagic"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void ResearchWithoutTopic_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            // "Research25" — digits with no topic between prefix and digits.
            var previews = ResultEffectsParser.ParseResearchProgress(
                ["Research25"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void NonResearchPrefix_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseResearchProgress(
                ["GiveTeleportationXp", "AddItemTSysPower(Foo,1)"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void NullOrEmptyEffects_ReturnsEmpty()
        {
            var refData = Phase7Fixture.Build();

            ResultEffectsParser.ParseResearchProgress(null, refData).Should().BeEmpty();
            ResultEffectsParser.ParseResearchProgress([], refData).Should().BeEmpty();
        }
    }

    public class XpGrantTests
    {
        [Fact]
        public void GiveTeleportationXp_IsEmittedAsTeleportationSkill()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseXpGrants(["GiveTeleportationXp"], refData);

            previews.Should().ContainSingle();
            previews[0].Skill.Should().Be("Teleportation");
            previews[0].DisplayLine.Should().Be("Grants Teleportation XP");
        }

        [Fact]
        public void HypotheticalGiveOtherSkillXp_IsRecognized()
        {
            // Future-proofing: the parser should accept any Give{Skill}Xp shape,
            // not just GiveTeleportationXp.
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseXpGrants(["GiveFireMagicXp"], refData);

            previews.Should().ContainSingle();
            previews[0].Skill.Should().Be("Fire Magic");
        }

        [Fact]
        public void NonGivePrefix_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseXpGrants(
                ["GiveTSysItem(Foo)", "AddItemTSysPower(Bar,1)"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void GivePrefixWithoutXpSuffix_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseXpGrants(
                ["GiveTeleportationFavor"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void EmptySkill_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            // "GiveXp" — no skill between prefix and suffix.
            var previews = ResultEffectsParser.ParseXpGrants(["GiveXp"], refData);

            previews.Should().BeEmpty();
        }
    }

    public class WordOfPowerTests
    {
        [Theory]
        [InlineData("DiscoverWordOfPower1", 1, "Discovers Word of Power #1")]
        [InlineData("DiscoverWordOfPower6", 6, "Discovers Word of Power #6")]
        public void IndexIsParsed(string raw, int expectedIndex, string expectedLine)
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseWordsOfPower([raw], refData);

            previews.Should().ContainSingle();
            previews[0].Index.Should().Be(expectedIndex);
            previews[0].DisplayLine.Should().Be(expectedLine);
        }

        [Fact]
        public void WithoutTrailingDigits_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseWordsOfPower(
                ["DiscoverWordOfPower"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void NonDiscoverPrefix_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseWordsOfPower(
                ["LearnAbility(Foo)", "ResearchFireMagic25"], refData);

            previews.Should().BeEmpty();
        }
    }

    public class LearnedAbilityTests
    {
        [Fact]
        public void AbilityIsParsedAndHumanized()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseLearnedAbilities(
                ["LearnAbility(FireBolt5)"], refData);

            previews.Should().ContainSingle();
            previews[0].AbilityInternalName.Should().Be("FireBolt5");
            previews[0].DisplayName.Should().Be("Fire Bolt 5");
            previews[0].DisplayLine.Should().Be("Teaches ability: Fire Bolt 5");
        }

        [Fact]
        public void EmptyArgs_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseLearnedAbilities(
                ["LearnAbility()"], refData);

            previews.Should().BeEmpty();
        }

        [Fact]
        public void NonLearnPrefix_IsSkipped()
        {
            var refData = Phase7Fixture.Build();

            var previews = ResultEffectsParser.ParseLearnedAbilities(
                ["BestowRecipeIfNotKnown(Foo)", "DiscoverWordOfPower3"], refData);

            previews.Should().BeEmpty();
        }
    }
}
