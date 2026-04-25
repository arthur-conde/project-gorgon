using Celebrimbor.ViewModels;
using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Celebrimbor.Tests;

public class AugmentPoolViewModelTests
{
    [Fact]
    public async Task ExpandsProfile_GroupsTiersByPower()
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

        // One card per power; both tiers live inside it, sorted ascending.
        vm.Groups.Should().ContainSingle();
        var group = vm.Groups[0];
        group.PowerInternalName.Should().Be("ArcheryBoost");
        group.Suffix.Should().Be("of Archery");
        group.Tiers.Should().HaveCount(2);
        group.Tiers[0].Tier.Should().Be(1);
        group.Tiers[1].Tier.Should().Be(2);
        group.Tiers.Select(t => t.EffectLines).Should().AllSatisfy(lines =>
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

        // Query is pre-populated; only the tier inside the bracket survives the per-tier filter.
        vm.QueryText.Should().Be("Tier >= 10 AND Tier <= 30");
        vm.Groups.Should().ContainSingle();
        vm.Groups[0].Tiers.Should().ContainSingle().Which.Tier.Should().Be(20);
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
        // Only tier 2 (level 40-60) brackets level 50; the group keeps just that tier.
        vm.Groups.Should().ContainSingle();
        vm.Groups[0].Tiers.Should().ContainSingle().Which.Should()
            .Match<PooledAugmentOption>(t => t.MinLevel == 40 && t.MaxLevel == 60);
    }

    [Fact]
    public async Task UnknownProfile_LeavesGroupsEmpty()
    {
        var refData = BuildFixture();
        var vm = new AugmentPoolViewModel("Source", "DoesNotExist", null, null, refData);
        await vm.LoadingTask;
        vm.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task FloorEffectLines_AreFromLowestVisibleTier()
    {
        var refData = BuildFixture(
            powers:
            [
                FakeReferenceData.Power("BiteHeal", "Werewolf", suffix: "Quality",
                    FakeReferenceData.Tier(7, "{BOOST_SKILL_ARCHERY}{14}"),
                    FakeReferenceData.Tier(8, "{BOOST_SKILL_ARCHERY}{15}"),
                    FakeReferenceData.Tier(9, "{BOOST_SKILL_ARCHERY}{17}"),
                    FakeReferenceData.Tier(10, "{BOOST_SKILL_ARCHERY}{18}")),
            ],
            attributes:
            [
                FakeReferenceData.Attribute("BOOST_SKILL_ARCHERY", "Health", iconId: 108),
            ],
            profiles: [("Pool", new[] { "BiteHeal" })]);

        // No filter — floor is the lowest absolute tier (7).
        var vm = new AugmentPoolViewModel("Source", "Pool", minTier: null, maxTier: null, refData);
        await vm.LoadingTask;
        vm.Groups[0].FloorEffectLines.Single().Text.Should().Contain("14");

        // Filter to tiers 9-10 — floor is now tier 9, not 7.
        vm.QueryText = "Tier >= 9";
        vm.Groups[0].MinTier.Should().Be(9);
        vm.Groups[0].FloorEffectLines.Single().Text.Should().Contain("17");
    }

    [Fact]
    public async Task GroupHeader_ReflectsFilteredTierAndLevelRanges()
    {
        var refData = BuildFixture(
            powers: [FakeReferenceData.Power("BiteHeal", "Werewolf", suffix: "Quality",
                FakeReferenceData.TierAt(7, 35, 50),
                FakeReferenceData.TierAt(8, 40, 55),
                FakeReferenceData.TierAt(9, 45, 60),
                FakeReferenceData.TierAt(10, 50, 65))],
            profiles: [("Pool", new[] { "BiteHeal" })]);

        var vm = new AugmentPoolViewModel("Source", "Pool", minTier: null, maxTier: null, refData);
        await vm.LoadingTask;

        // Unfiltered: full range.
        vm.Groups[0].TierRange.Should().Be("Tier 7-10");
        vm.Groups[0].LevelRange.Should().Be("Lvl 35-65");
        vm.Groups[0].RangesLine.Should().Be("Tier 7-10 · Lvl 35-65");

        // Narrow to a single tier — header collapses.
        vm.QueryText = "Tier = 9";
        vm.Groups[0].TierRange.Should().Be("Tier 9");
        vm.Groups[0].LevelRange.Should().Be("Lvl 45-60");
    }

    [Fact]
    public async Task Filter_HidesGroupEntirely_WhenAllTiersExcluded()
    {
        var refData = BuildFixture(
            powers:
            [
                FakeReferenceData.Power("WerewolfPower", "Werewolf", suffix: null, FakeReferenceData.Tier(1)),
                FakeReferenceData.Power("SwordPower", "Sword", suffix: null, FakeReferenceData.Tier(1)),
            ],
            profiles: [("Pool", new[] { "WerewolfPower", "SwordPower" })]);

        var vm = new AugmentPoolViewModel("Source", "Pool", minTier: null, maxTier: null, refData);
        await vm.LoadingTask;
        vm.Groups.Should().HaveCount(2);

        vm.QueryText = "Skill = \"Werewolf\"";
        vm.Groups.Should().ContainSingle().Which.PowerInternalName.Should().Be("WerewolfPower");
    }

    [Fact]
    public async Task Subtitle_ShowsFilteredAndTotalCounts()
    {
        var refData = BuildFixture(
            powers:
            [
                FakeReferenceData.Power("A", "Werewolf", suffix: null, FakeReferenceData.Tier(1), FakeReferenceData.Tier(2)),
                FakeReferenceData.Power("B", "Sword", suffix: null, FakeReferenceData.Tier(1)),
            ],
            profiles: [("Pool", new[] { "A", "B" })]);

        var vm = new AugmentPoolViewModel("Source", "Pool", minTier: null, maxTier: null, refData);
        await vm.LoadingTask;

        // Unfiltered — collapsed format.
        vm.Subtitle.Should().Contain("2 powers · 3 tier rows");
        vm.Subtitle.Should().NotContain(" of ");

        // Filtered — "X of Y" format surfaces what the query hid.
        vm.QueryText = "Skill = \"Werewolf\"";
        vm.Subtitle.Should().Contain("1 of 2 powers · 2 of 3 tier rows");
    }

    [Fact]
    public async Task SourceEquipSlot_PreFillsSlotsContainsClause()
    {
        // Issue #8: opening a pool for a Necklace template seeds Slots contains "Necklace"
        // and filters out powers whose Slots list excludes Necklace.
        var refData = BuildFixture(
            powers:
            [
                FakeReferenceData.Power("ParryRiposteBoostTrauma", "Sword", suffix: null,
                    slots: ["MainHand", "Ring"],
                    FakeReferenceData.Tier(1, "{MAX_ARMOR}{1}")),
                FakeReferenceData.Power("NecklaceArmor", "General", suffix: null,
                    slots: ["Necklace"],
                    FakeReferenceData.Tier(1, "{MAX_ARMOR}{5}")),
            ],
            profiles: [("MyPool", new[] { "ParryRiposteBoostTrauma", "NecklaceArmor" })]);

        var vm = new AugmentPoolViewModel(
            "Source", "MyPool",
            minTier: null, maxTier: null,
            recommendedSkill: null, craftingTargetLevel: null, rolledRarityRank: null,
            sourceEquipSlot: "Necklace",
            refData);
        await vm.LoadingTask;

        vm.QueryText.Should().Be("Slots contains \"Necklace\"");
        vm.Groups.Should().ContainSingle()
            .Which.PowerInternalName.Should().Be("NecklaceArmor");
    }

    [Fact]
    public async Task SourceEquipSlot_ClearingClauseRestoresUnfilteredPool()
    {
        var refData = BuildFixture(
            powers:
            [
                FakeReferenceData.Power("Power_MainHand", "Sword", suffix: null,
                    slots: ["MainHand"],
                    FakeReferenceData.Tier(1, "{MAX_ARMOR}{1}")),
                FakeReferenceData.Power("Power_Necklace", "General", suffix: null,
                    slots: ["Necklace"],
                    FakeReferenceData.Tier(1, "{MAX_ARMOR}{5}")),
            ],
            profiles: [("MyPool", new[] { "Power_MainHand", "Power_Necklace" })]);

        var vm = new AugmentPoolViewModel(
            "Source", "MyPool",
            minTier: null, maxTier: null,
            recommendedSkill: null, craftingTargetLevel: null, rolledRarityRank: null,
            sourceEquipSlot: "Necklace",
            refData);
        await vm.LoadingTask;

        vm.Groups.Should().ContainSingle();
        vm.QueryText = "";
        vm.Groups.Should().HaveCount(2);
    }

    [Fact]
    public async Task SourceEquipSlot_CombinesWithRecommendedSkill()
    {
        var refData = BuildFixture(
            powers:
            [
                FakeReferenceData.Power("WerewolfNecklace", "Werewolf", suffix: null,
                    slots: ["Necklace"],
                    FakeReferenceData.Tier(1, "{MAX_ARMOR}{1}")),
            ],
            profiles: [("All", new[] { "WerewolfNecklace" })]);

        var vm = new AugmentPoolViewModel(
            "Source", "All",
            minTier: null, maxTier: null,
            recommendedSkill: "Werewolf",
            craftingTargetLevel: null,
            rolledRarityRank: null,
            sourceEquipSlot: "Necklace",
            refData);
        await vm.LoadingTask;

        vm.QueryText.Should().Be("Skill = \"Werewolf\" AND Slots contains \"Necklace\"");
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
