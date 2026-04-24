using FluentAssertions;
using Gorgon.Shared.Reference;
using Xunit;

namespace Gorgon.Shared.Tests.Reference;

public class AugmentParserTests
{
    [Fact]
    public void WellFormedEntry_ReturnsPreviewWithRenderedEffectLine()
    {
        var refData = Fake(
            powers: [Power("ShamanicHeadArmor", "ShamanicInfusion", suffix: null,
                tiers: [Tier(1, ["{MAX_ARMOR}{13}"]), Tier(2, ["{MAX_ARMOR}{25}"])])],
            attributes: [new AttributeEntry("MAX_ARMOR", "Max Armor", "AsInt", "Always", null, [101])]);

        var previews = ResultEffectsParser.ParseAugments(
            ["AddItemTSysPower(ShamanicHeadArmor,2)"], refData);

        previews.Should().ContainSingle();
        var preview = previews[0];
        preview.PowerInternalName.Should().Be("ShamanicHeadArmor");
        preview.Suffix.Should().BeNull();
        preview.Tier.Should().Be(2);
        preview.EffectLines.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new EffectLine(101, "Max Armor: 25"));
    }

    [Fact]
    public void SuffixIsPropagatedFromPower()
    {
        var refData = Fake(
            powers: [Power("ArcheryBoost", "Archery", suffix: "of Archery",
                tiers: [Tier(1, ["{BOOST_SKILL_ARCHERY}{5}"])])],
            attributes: [new AttributeEntry("BOOST_SKILL_ARCHERY", "Archery Damage", "AsBuffDelta", "IfNotZero", null, [108])]);

        var previews = ResultEffectsParser.ParseAugments(
            ["AddItemTSysPower(ArcheryBoost,1)"], refData);

        previews.Should().ContainSingle().Which.Suffix.Should().Be("of Archery");
    }

    [Fact]
    public void UnknownPrefix_IsSkipped()
    {
        var refData = Fake();

        var previews = ResultEffectsParser.ParseAugments(
            ["BestowRecipeIfNotKnown(recipe_next)", "DispelCalligraphyA()"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void UnresolvablePowerName_IsSkipped()
    {
        var refData = Fake(
            powers: [Power("ArcheryBoost", "Archery", suffix: "of Archery",
                tiers: [Tier(1, [])])]);

        var previews = ResultEffectsParser.ParseAugments(
            ["AddItemTSysPower(DoesNotExistPower,1)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void TierOutsidePool_IsSkipped()
    {
        var refData = Fake(
            powers: [Power("ShamanicHeadArmor", "ShamanicInfusion", suffix: null,
                tiers: [Tier(1, ["{MAX_ARMOR}{13}"])])],
            attributes: [new AttributeEntry("MAX_ARMOR", "Max Armor", "AsInt", "Always", null, [101])]);

        var previews = ResultEffectsParser.ParseAugments(
            ["AddItemTSysPower(ShamanicHeadArmor,99)"], refData);

        previews.Should().BeEmpty();
    }

    [Theory]
    [InlineData("AddItemTSysPower")]
    [InlineData("AddItemTSysPower(")]
    [InlineData("AddItemTSysPower()")]
    [InlineData("AddItemTSysPower(,1)")]
    [InlineData("AddItemTSysPower(ShamanicHeadArmor)")]
    [InlineData("AddItemTSysPower(ShamanicHeadArmor,notanumber)")]
    [InlineData("")]
    [InlineData("   ")]
    public void MalformedInput_IsSkippedSilently(string effect)
    {
        var refData = Fake(
            powers: [Power("ShamanicHeadArmor", "ShamanicInfusion", suffix: null,
                tiers: [Tier(1, ["{MAX_ARMOR}{13}"])])],
            attributes: [new AttributeEntry("MAX_ARMOR", "Max Armor", "AsInt", "Always", null, [101])]);

        ResultEffectsParser.ParseAugments([effect], refData).Should().BeEmpty();
    }

    [Fact]
    public void NullOrEmptyInput_ReturnsEmpty()
    {
        var refData = Fake();

        ResultEffectsParser.ParseAugments(null, refData).Should().BeEmpty();
        ResultEffectsParser.ParseAugments([], refData).Should().BeEmpty();
    }

    [Fact]
    public void MixedEffects_ParseCraftedGearAndParseAugmentsPartitionOutput()
    {
        var refData = Fake(
            items: [Item(1, "CraftedLongsword3", "Longsword")],
            powers: [Power("WeaponBoost", "Sword", suffix: "of Sharpness",
                tiers: [Tier(2, ["{MAX_ARMOR}{5}"])])],
            attributes: [new AttributeEntry("MAX_ARMOR", "Max Armor", "AsInt", "Always", null, [101])]);

        string[] effects =
        [
            "DispelCalligraphyA()",
            "TSysCraftedEquipment(CraftedLongsword3,3)",
            "AddItemTSysPower(WeaponBoost,2)",
            "BestowRecipeIfNotKnown(recipe_next)"
        ];

        ResultEffectsParser.ParseCraftedGear(effects, refData)
            .Should().ContainSingle().Which.InternalName.Should().Be("CraftedLongsword3");
        ResultEffectsParser.ParseAugments(effects, refData)
            .Should().ContainSingle().Which.PowerInternalName.Should().Be("WeaponBoost");
    }

    [Fact]
    public void DisplayLine_FallsBackToInternalName_WhenSuffixMissing()
    {
        var preview = new AugmentPreview("ShamanicHeadArmor", Suffix: null, Tier: 2, EffectLines: []);
        preview.DisplayLine.Should().Be("ShamanicHeadArmor · Tier 2");
    }

    [Fact]
    public void DisplayLine_UsesSuffix_WhenPresent()
    {
        var preview = new AugmentPreview("ArcheryBoost", Suffix: "of Archery", Tier: 1, EffectLines: []);
        preview.DisplayLine.Should().Be("of Archery · Tier 1");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ItemEntry Item(long id, string internalName, string displayName, int icon = 0)
        => new(
            Id: id,
            Name: displayName,
            InternalName: internalName,
            MaxStackSize: 50,
            IconId: icon,
            Keywords: []);

    private static PowerTier Tier(int tier, string[] effectDescs, int maxLevel = 0)
        => new(tier, effectDescs, maxLevel);

    private static PowerEntry Power(string internalName, string skill, string? suffix, PowerTier[] tiers)
        => new(internalName, skill, Slots: [], Suffix: suffix,
            Tiers: tiers.ToDictionary(t => t.Tier));

    private static IReferenceDataService Fake(
        ItemEntry[]? items = null,
        PowerEntry[]? powers = null,
        AttributeEntry[]? attributes = null)
        => new MinimalRefData(items ?? [], powers ?? [], attributes ?? []);

    private sealed class MinimalRefData : IReferenceDataService
    {
        public MinimalRefData(IEnumerable<ItemEntry> items, IEnumerable<PowerEntry> powers, IEnumerable<AttributeEntry> attributes)
        {
            Items = items.ToDictionary(i => i.Id);
            ItemsByInternalName = items.ToDictionary(i => i.InternalName, StringComparer.Ordinal);
            Powers = powers.ToDictionary(p => p.InternalName, StringComparer.Ordinal);
            Attributes = attributes.ToDictionary(a => a.Token, StringComparer.Ordinal);
        }

        public IReadOnlyList<string> Keys => [];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; }
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; }
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; }
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; }

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
