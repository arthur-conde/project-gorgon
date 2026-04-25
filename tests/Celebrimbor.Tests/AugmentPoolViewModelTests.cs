using Celebrimbor.ViewModels;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Celebrimbor.Tests;

public class AugmentPoolViewModelTests
{
    [Fact]
    public async Task ExpandsProfile_RendersOneOptionPerTier()
    {
        var refData = BuildFixture(
            powers:
            [
                FakeReferenceData.Power("ArcheryBoost", "Archery", suffix: "of Archery",
                    FakeReferenceData.Tier(1, "{BOOST_SKILL_ARCHERY}{5}"),
                    FakeReferenceData.Tier(2, "{BOOST_SKILL_ARCHERY}{12}")),
            ],
            attributes:
            [
                FakeReferenceData.Attribute("BOOST_SKILL_ARCHERY", "Archery Damage", iconId: 108),
            ],
            profiles: [("Weapon", new[] { "ArcheryBoost" })]);

        var vm = new AugmentPoolViewModel("Source", "Weapon", minTier: null, maxTier: null, refData);
        await vm.LoadingTask;

        vm.Options.Should().HaveCount(2);
        vm.Options[0].Tier.Should().Be(1);
        vm.Options[1].Tier.Should().Be(2);
        vm.Options.Select(o => o.EffectLines).Should().AllSatisfy(lines =>
            lines.Should().ContainSingle().Which.Text.Should().StartWith("Archery Damage"));
    }

    [Fact]
    public async Task ExtractRecipe_PreFillsQueryTextWithTierBracket()
    {
        var refData = BuildFixture(
            powers:
            [
                FakeReferenceData.Power("X", "Sword", suffix: null,
                    FakeReferenceData.Tier(1), FakeReferenceData.Tier(20), FakeReferenceData.Tier(50)),
            ],
            profiles: [("Pool", new[] { "X" })]);

        var vm = new AugmentPoolViewModel("Source", "Pool", minTier: 10, maxTier: 30, recommendedSkill: null, craftingTargetLevel: null, refData);
        await vm.LoadingTask;

        // Query is pre-populated; the grid applies it on top of the materialized list.
        vm.QueryText.Should().Be("Tier >= 10 AND Tier <= 30");
        // All tiers materialize so the user can clear/widen the query and explore.
        vm.Options.Should().HaveCount(3);
    }

    [Fact]
    public async Task EnchantmentRecipe_StartsWithEmptyQueryText_WhenNoRecommendedSkill()
    {
        var refData = BuildFixture(
            powers: [FakeReferenceData.Power("X", "Sword", suffix: null, FakeReferenceData.Tier(1))],
            profiles: [("Pool", new[] { "X" })]);

        var vm = new AugmentPoolViewModel("Source", "Pool", minTier: null, maxTier: null, recommendedSkill: null, craftingTargetLevel: null, refData);
        await vm.LoadingTask;

        vm.QueryText.Should().BeEmpty();
    }

    [Fact]
    public async Task EnchantmentRecipe_PreFillsSkillFilterFromRecommendedSkill()
    {
        var refData = BuildFixture(
            powers: [FakeReferenceData.Power("X", "Werewolf", suffix: null, FakeReferenceData.Tier(1))],
            profiles: [("All", new[] { "X" })]);

        var vm = new AugmentPoolViewModel("Source", "All", minTier: null, maxTier: null, recommendedSkill: "Werewolf", craftingTargetLevel: null, refData);
        await vm.LoadingTask;

        vm.QueryText.Should().Be("Skill = \"Werewolf\"");
    }

    [Fact]
    public async Task ExtractRecipe_CombinesTierBracketAndSkillFilter()
    {
        var refData = BuildFixture(
            powers: [FakeReferenceData.Power("X", "Sword", suffix: null, FakeReferenceData.Tier(20))],
            profiles: [("Pool", new[] { "X" })]);

        var vm = new AugmentPoolViewModel("Source", "Pool", minTier: 0, maxTier: 50, recommendedSkill: "Sword", craftingTargetLevel: null, refData);
        await vm.LoadingTask;

        vm.QueryText.Should().Be("Tier >= 0 AND Tier <= 50 AND Skill = \"Sword\"");
    }

    [Fact]
    public async Task EnchantmentRecipe_PreFillsLevelBracketFromCraftingTargetLevel()
    {
        var refData = BuildFixture(
            powers: [FakeReferenceData.Power("X", "Werewolf", suffix: null,
                FakeReferenceData.TierAt(1, 10, 30),
                FakeReferenceData.TierAt(2, 40, 60),
                FakeReferenceData.TierAt(3, 70, 90))],
            profiles: [("All", new[] { "X" })]);

        var vm = new AugmentPoolViewModel("Source", "All", minTier: null, maxTier: null,
            recommendedSkill: "Werewolf", craftingTargetLevel: 50, refData);
        await vm.LoadingTask;

        vm.QueryText.Should().Be("MinLevel <= 50 AND MaxLevel >= 50 AND Skill = \"Werewolf\"");
        // All 3 tiers materialize; the grid query restricts the view to the eligible one.
        vm.Options.Should().HaveCount(3);
        vm.Options.Should().ContainSingle(o => o.MinLevel == 40 && o.MaxLevel == 60);
    }

    [Fact]
    public async Task UnknownProfile_LeavesOptionsEmpty()
    {
        var refData = BuildFixture();
        var vm = new AugmentPoolViewModel("Source", "DoesNotExist", null, null, refData);
        await vm.LoadingTask;
        vm.Options.Should().BeEmpty();
    }

    private static FakeReferenceData BuildFixture(
        IEnumerable<PowerEntry>? powers = null,
        IEnumerable<AttributeEntry>? attributes = null,
        IEnumerable<(string profile, string[] powers)>? profiles = null)
    {
        var profileDict = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (profiles is not null)
        {
            foreach (var (name, names) in profiles)
                profileDict[name] = names;
        }
        return new FakeReferenceData(
            items: [],
            recipes: [],
            powers: powers,
            attributes: attributes,
            profiles: profileDict);
    }
}
