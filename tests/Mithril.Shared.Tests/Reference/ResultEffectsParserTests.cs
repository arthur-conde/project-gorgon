using FluentAssertions;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Reference;

public class ResultEffectsParserTests
{
    [Fact]
    public void SingleArg_ReturnsPreviewWithNullTierAndSubtype()
    {
        var refData = Fake(Item(1, "CraftedLeatherBoots1", "Leather Boots", icon: 111));

        var previews = ResultEffectsParser.ParseCraftedGear(
            ["TSysCraftedEquipment(CraftedLeatherBoots1)"], refData);

        previews.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new CraftedGearPreview("CraftedLeatherBoots1", "Leather Boots", 111, null, null));
    }

    [Fact]
    public void TwoArgs_CarriesTier()
    {
        var refData = Fake(Item(1, "CraftedLeatherBoots5", "Leather Boots", icon: 222));

        var previews = ResultEffectsParser.ParseCraftedGear(
            ["TSysCraftedEquipment(CraftedLeatherBoots5,1)"], refData);

        previews.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new CraftedGearPreview("CraftedLeatherBoots5", "Leather Boots", 222, 1, null));
    }

    [Fact]
    public void ThreeArgs_CarriesTierAndSubtype()
    {
        var refData = Fake(Item(1, "CraftedWerewolfShoes1", "Werewolf Shoes", icon: 333));

        var previews = ResultEffectsParser.ParseCraftedGear(
            ["TSysCraftedEquipment(CraftedWerewolfShoes1,0,Werewolf)"], refData);

        previews.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new CraftedGearPreview("CraftedWerewolfShoes1", "Werewolf Shoes", 333, 0, "Werewolf"));
    }

    [Fact]
    public void UnknownPrefix_ReturnsEmpty()
    {
        var refData = Fake(Item(1, "CraftedLeatherBoots1", "Leather Boots"));

        var previews = ResultEffectsParser.ParseCraftedGear(
            ["DispelCalligraphyA()", "GiveTeleportationXp()"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void UnresolvableTemplate_IsSkippedSilently()
    {
        var refData = Fake(Item(1, "CraftedLeatherBoots1", "Leather Boots"));

        var previews = ResultEffectsParser.ParseCraftedGear(
            ["TSysCraftedEquipment(DoesNotExistInItems,3,Werewolf)"], refData);

        previews.Should().BeEmpty();
    }

    [Theory]
    [InlineData("TSysCraftedEquipment")]
    [InlineData("TSysCraftedEquipment(")]
    [InlineData("TSysCraftedEquipment()")]
    [InlineData("TSysCraftedEquipment(,0,Werewolf)")]
    [InlineData("")]
    [InlineData("   ")]
    public void MalformedInput_IsSkippedSilently(string effect)
    {
        var refData = Fake(Item(1, "CraftedLeatherBoots1", "Leather Boots"));

        var previews = ResultEffectsParser.ParseCraftedGear([effect], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void NonIntegerTier_DropsToNull()
    {
        var refData = Fake(Item(1, "CraftedLeatherBoots1", "Leather Boots"));

        var previews = ResultEffectsParser.ParseCraftedGear(
            ["TSysCraftedEquipment(CraftedLeatherBoots1,notanumber,Werewolf)"], refData);

        previews.Should().ContainSingle().Which.Tier.Should().BeNull();
        previews[0].Subtype.Should().Be("Werewolf");
    }

    [Fact]
    public void MixedEffects_ReturnsOnlyCraftedGear()
    {
        var refData = Fake(Item(1, "CraftedLongsword3", "Longsword"));

        var previews = ResultEffectsParser.ParseCraftedGear(
        [
            "DispelCalligraphyA()",
            "TSysCraftedEquipment(CraftedLongsword3,3)",
            "BestowRecipeIfNotKnown(recipe_next)"
        ], refData);

        previews.Should().ContainSingle().Which.InternalName.Should().Be("CraftedLongsword3");
    }

    [Fact]
    public void GiveTSysItem_FoldsIntoCraftedGearWithNullTier()
    {
        var refData = Fake(Item(99, "Horseshoes", "Horseshoes", icon: 555));

        var previews = ResultEffectsParser.ParseCraftedGear(
            ["GiveTSysItem(Horseshoes)"], refData);

        previews.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new CraftedGearPreview("Horseshoes", "Horseshoes", 555, null, null));
    }

    [Fact]
    public void CraftSimpleTSysItem_FoldsIntoCraftedGearWithNullTier()
    {
        var refData = Fake(Item(42, "ArachnidHarness", "Arachnid Harness", icon: 600));

        var previews = ResultEffectsParser.ParseCraftedGear(
            ["CraftSimpleTSysItem(ArachnidHarness)"], refData);

        previews.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new CraftedGearPreview("ArachnidHarness", "Arachnid Harness", 600, null, null));
    }

    [Fact]
    public void GiveTSysItem_UnknownTemplate_IsSkippedSilently()
    {
        var refData = Fake(Item(1, "Horseshoes", "Horseshoes"));

        var previews = ResultEffectsParser.ParseCraftedGear(
            ["GiveTSysItem(NotInItems)", "CraftSimpleTSysItem(AlsoMissing)"], refData);

        previews.Should().BeEmpty();
    }

    [Fact]
    public void NullOrEmptyInput_ReturnsEmpty()
    {
        var refData = Fake();

        ResultEffectsParser.ParseCraftedGear(null, refData).Should().BeEmpty();
        ResultEffectsParser.ParseCraftedGear([], refData).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, null, "Leather Boots")]
    [InlineData(5, null, "Leather Boots · Tier 5")]
    [InlineData(null, "Werewolf", "Leather Boots · Werewolf")]
    [InlineData(6, "Werewolf", "Leather Boots · Tier 6 · Werewolf")]
    public void DisplayLine_FormatsTuple(int? tier, string? subtype, string expected)
    {
        var preview = new CraftedGearPreview("CraftedLeatherBoots1", "Leather Boots", 111, tier, subtype);

        preview.DisplayLine.Should().Be(expected);
    }

    private static ItemEntry Item(long id, string internalName, string displayName, int icon = 0)
        => new(
            Id: id,
            Name: displayName,
            InternalName: internalName,
            MaxStackSize: 50,
            IconId: icon,
            Keywords: []);

    private static IReferenceDataService Fake(params ItemEntry[] items)
        => new MinimalRefData(items);

    private sealed class MinimalRefData : IReferenceDataService
    {
        public MinimalRefData(IEnumerable<ItemEntry> items)
        {
            Items = items.ToDictionary(i => i.Id);
            ItemsByInternalName = items.ToDictionary(i => i.InternalName, StringComparer.Ordinal);
        }

        public IReadOnlyList<string> Keys => [];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; }
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; }
        public ItemKeywordIndex KeywordIndex => new(Items);
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
