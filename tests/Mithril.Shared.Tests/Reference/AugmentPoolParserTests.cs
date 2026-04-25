using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class AugmentPoolParserTests
{
    [Fact]
    public void ExtractTSysPower_DoesNotEmitPool()
    {
        // ExtractTSysPower no longer flows through ParseAugmentPools — its rolled
        // power is determined by the player-provided cube at craft time, so there's
        // no static pool to render. Coverage now lives in
        // ParseUnpreviewableExtractions; see UnpreviewableExtractionParserTests.
        var refData = Phase7Fixture.Build(
            items: [Phase7Fixture.Item(1, "MainHandAugment", "Main-Hand Augment", tsysProfile: "TestWeapon")],
            profiles: new Dictionary<string, IReadOnlyList<string>>
            {
                ["TestWeapon"] = ["AnyPower"],
            });

        var pools = ResultEffectsParser.ParseAugmentPools(
            ["ExtractTSysPower(MainHandAugment,WeaponAugmentBrewing,0,30)"], refData);

        pools.Should().BeEmpty();
    }

    [Fact]
    public void TSysCraftedEquipment_OnTemplateWithProfile_EmitsPoolWithoutTierBracket()
    {
        var refData = Phase7Fixture.Build(
            items: [Phase7Fixture.Item(1, "CraftedLeatherBoots1", "Crafted Leather Boots", tsysProfile: "All")],
            profiles: new Dictionary<string, IReadOnlyList<string>>
            {
                ["All"] = ["P1", "P2", "P3"],
            });

        var pools = ResultEffectsParser.ParseAugmentPools(
            ["TSysCraftedEquipment(CraftedLeatherBoots1)"], refData);

        pools.Should().ContainSingle();
        var pool = pools[0];
        pool.ProfileName.Should().Be("All");
        pool.MinTier.Should().BeNull();
        pool.MaxTier.Should().BeNull();
        // 3 distinct powers eligible to roll. Tier is determined by the craft, not rolled.
        pool.OptionCount.Should().Be(3);
        pool.SourceLabel.Should().Be("Possible rolls for Crafted Leather Boots");
    }

    [Fact]
    public void TSysCraftedEquipment_PullsRecommendedSkillFromArg3SubtypeOnly()
    {
        // arg3 ("Werewolf") is the treasure-system gate; SkillReqs is a wearer gate
        // and must NOT leak into the pool filter.
        var refData = Phase7Fixture.Build(
            items: [Phase7Fixture.Item(1, "WerewolfChest", "Werewolf Chest", tsysProfile: "All",
                skillReqs: new Dictionary<string, int> { ["Werewolf"] = 50 })],
            profiles: new Dictionary<string, IReadOnlyList<string>>
            {
                ["All"] = ["P1"],
            });

        var withSubtype = ResultEffectsParser.ParseAugmentPools(
            ["TSysCraftedEquipment(WerewolfChest,0,Werewolf)"], refData).Single();
        withSubtype.RecommendedSkill.Should().Be("Werewolf");

        // Same template, no arg3 → no roll-time gate, no pre-filter.
        var withoutSubtype = ResultEffectsParser.ParseAugmentPools(
            ["TSysCraftedEquipment(WerewolfChest)"], refData).Single();
        withoutSubtype.RecommendedSkill.Should().BeNull();

        // arg2 alone (rarity bump) without arg3 also leaves it ungated.
        var rarityOnly = ResultEffectsParser.ParseAugmentPools(
            ["TSysCraftedEquipment(WerewolfChest,1)"], refData).Single();
        rarityOnly.RecommendedSkill.Should().BeNull();
    }

    [Fact]
    public void TSysCraftedEquipment_PullsCraftingTargetLevelFromTemplate()
    {
        var refData = Phase7Fixture.Build(
            items: [Phase7Fixture.Item(1, "WerewolfChest", "Werewolf Chest", tsysProfile: "All",
                craftingTargetLevel: 50)],
            profiles: new Dictionary<string, IReadOnlyList<string>>
            {
                ["All"] = ["P1"],
            });

        var pool = ResultEffectsParser.ParseAugmentPools(
            ["TSysCraftedEquipment(WerewolfChest)"], refData).Single();

        pool.CraftingTargetLevel.Should().Be(50);
    }

    [Fact]
    public void TSysCraftedEquipment_OnTemplateWithoutProfile_IsSkipped()
    {
        var refData = Phase7Fixture.Build(
            items: [Phase7Fixture.Item(1, "ShoddyLeatherBoots", "Shoddy Leather Boots", tsysProfile: null)]);

        var pools = ResultEffectsParser.ParseAugmentPools(
            ["TSysCraftedEquipment(ShoddyLeatherBoots)"], refData);

        pools.Should().BeEmpty();
    }

    [Fact]
    public void UnknownProfileName_IsSkipped()
    {
        var refData = Phase7Fixture.Build(
            items: [Phase7Fixture.Item(1, "WeirdItem", "Weird Item", tsysProfile: "NotInProfilesJson")]);

        var pools = ResultEffectsParser.ParseAugmentPools(
            ["TSysCraftedEquipment(WeirdItem)"], refData);

        pools.Should().BeEmpty();
    }

    [Fact]
    public void EmptyProfileList_IsSkipped()
    {
        var refData = Phase7Fixture.Build(
            items: [Phase7Fixture.Item(1, "Empty", "Empty", tsysProfile: "EmptyPool")],
            profiles: new Dictionary<string, IReadOnlyList<string>>
            {
                ["EmptyPool"] = [],
            });

        var pools = ResultEffectsParser.ParseAugmentPools(
            ["TSysCraftedEquipment(Empty)"], refData);

        pools.Should().BeEmpty();
    }

}
