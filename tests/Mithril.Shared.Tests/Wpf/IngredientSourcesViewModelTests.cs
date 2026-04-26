using FluentAssertions;
using Mithril.Shared.Reference;
using Mithril.Shared.Storage;
using Mithril.Shared.Wpf;
using Xunit;

namespace Mithril.Shared.Tests.Wpf;

public class IngredientSourcesViewModelTests
{
    [Fact]
    public void Single_item_row_groups_OnHand_by_location_and_sorts_items()
    {
        var input = new IngredientSourcesInput(
            Title: "Iron Bar",
            KeywordsLabel: null,
            ItemInternalName: "IronBar",
            OnHand:
            [
                new IngredientLocation("Inventory", 5, "IronBar", "Iron Bar", IconId: 100),
                new IngredientLocation("Serbule Chest", 12, "IronBar", "Iron Bar", IconId: 100),
            ]);

        var vm = IngredientSourcesViewModel.Build(input, EmptyRefData.Instance);

        vm.OnHand.Should().HaveCount(2);
        vm.OnHandTotal.Should().Be(17);
        // Larger location bucket sorts first.
        vm.OnHand[0].Label.Should().Be("Serbule Chest");
        vm.OnHand[0].TotalQuantity.Should().Be(12);
        vm.OnHand[0].Items.Should().ContainSingle().Which.DisplayName.Should().Be("Iron Bar");
    }

    [Fact]
    public void Keyword_row_collapses_per_item_breakdown_per_location()
    {
        // Two items keyworded Crystal, both stored in Serbule Chest, plus one in inventory.
        var input = new IngredientSourcesInput(
            Title: "Auxiliary Crystal",
            KeywordsLabel: "any Crystal",
            ItemInternalName: null,
            OnHand:
            [
                new IngredientLocation("Serbule Chest", 47, "RoughCrystal", "Rough Crystal", 200),
                new IngredientLocation("Serbule Chest", 33, "PolishedCrystal", "Polished Crystal", 201),
                new IngredientLocation("Inventory", 12, "RoughCrystal", "Rough Crystal", 200),
            ]);

        var vm = IngredientSourcesViewModel.Build(input, EmptyRefData.Instance);

        vm.OnHandTotal.Should().Be(92);
        vm.OnHand.Should().HaveCount(2);
        var serbule = vm.OnHand.Single(g => g.Label == "Serbule Chest");
        serbule.TotalQuantity.Should().Be(80);
        serbule.Items.Should().HaveCount(2);
        // Items inside a bucket are sorted by quantity desc.
        serbule.Items[0].DisplayName.Should().Be("Rough Crystal");
        serbule.Items[0].Quantity.Should().Be(47);
    }

    [Fact]
    public void Keyword_row_shows_placeholder_for_Sources()
    {
        var input = new IngredientSourcesInput("Auxiliary Crystal", "any Crystal", ItemInternalName: null, OnHand: []);
        var vm = IngredientSourcesViewModel.Build(input, EmptyRefData.Instance);

        vm.Sources.Should().BeEmpty();
        vm.HasSourcesPlaceholder.Should().BeTrue();
        vm.SourcesPlaceholder.Should().Contain("not aggregated yet");
    }

    [Fact]
    public void Default_tab_is_OnHand_when_stock_exists()
    {
        var input = new IngredientSourcesInput(
            Title: "Iron Bar",
            KeywordsLabel: null,
            ItemInternalName: "IronBar",
            OnHand: [new IngredientLocation("Inventory", 5, "IronBar", "Iron Bar", 100)]);

        var vm = IngredientSourcesViewModel.Build(input, EmptyRefData.Instance);

        vm.SelectedTabIndex.Should().Be(0);
    }

    [Fact]
    public void Default_tab_is_Sources_when_no_on_hand_stock()
    {
        var input = new IngredientSourcesInput("Iron Bar", null, "IronBar", []);
        var vm = IngredientSourcesViewModel.Build(input, EmptyRefData.Instance);

        vm.SelectedTabIndex.Should().Be(1);
    }

    [Fact]
    public void Vendor_source_resolves_NPC_name_and_area()
    {
        var npc = new NpcEntry(
            Key: "NPC_Hulon",
            Name: "Hulon the Huge",
            Area: "Serbule",
            Preferences: [],
            ItemGiftTiers: [],
            Services: []);

        var refData = new StubRefData(
            sources: new()
            {
                ["IronBar"] = [new ItemSource("Vendor", "NPC_Hulon", null)],
            },
            npcs: new() { ["NPC_Hulon"] = npc });

        var input = new IngredientSourcesInput("Iron Bar", null, "IronBar", []);
        var vm = IngredientSourcesViewModel.Build(input, refData);

        var source = vm.Sources.Should().ContainSingle().Subject;
        source.Kind.Should().Be("Vendor");
        source.Label.Should().Be("Hulon the Huge");
        source.AreaFriendlyName.Should().Be("Serbule");
        // Service has no MinFavorTier → no requirement line.
        source.Requirement.Should().BeNull();
    }

    [Fact]
    public void Vendor_source_surfaces_Store_service_MinFavorTier_as_Requirement()
    {
        // Hulon's Store service is gated on "Liked" favor; we should expose that.
        var store = new NpcService(Type: "Store", MinFavorTier: "Liked", CapIncreases: []);
        var npc = new NpcEntry("NPC_Hulon", "Hulon", "Serbule", Preferences: [], ItemGiftTiers: [], Services: [store]);
        var refData = new StubRefData(
            sources: new() { ["IronBar"] = [new ItemSource("Vendor", "NPC_Hulon", null)] },
            npcs: new() { ["NPC_Hulon"] = npc });

        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("Iron Bar", null, "IronBar", []), refData);

        vm.Sources.Single().Requirement.Should().Be("Requires Liked or higher");
    }

    [Fact]
    public void Barter_source_uses_Barter_service_for_Requirement_lookup()
    {
        // Same NPC has a Store at Neutral and a Barter at "Loved" — only the Barter gate
        // applies for a Barter source. Confirms the service-type routing isn't conflated.
        var store = new NpcService("Store", MinFavorTier: null, CapIncreases: []);
        var barter = new NpcService("Barter", MinFavorTier: "Loved", CapIncreases: []);
        var npc = new NpcEntry("NPC_Velkort", "Velkort", "Serbule", Preferences: [], ItemGiftTiers: [], Services: [store, barter]);
        var refData = new StubRefData(
            sources: new() { ["RareIngot"] = [new ItemSource("Barter", "NPC_Velkort", null)] },
            npcs: new() { ["NPC_Velkort"] = npc });

        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("Rare Ingot", null, "RareIngot", []), refData);

        vm.Sources.Single().Requirement.Should().Be("Requires Loved or higher");
    }

    [Fact]
    public void NpcGift_source_does_not_emit_per_npc_favor_Requirement()
    {
        // NpcGift gating lives on per-preference RequiredFavorTier, not per-NPC.
        // The Sources tab line should not claim a vendor-style favor gate for gifts.
        var store = new NpcService("Store", MinFavorTier: "Liked", CapIncreases: []);
        var npc = new NpcEntry("NPC_Marna", "Marna", "Serbule", Preferences: [], ItemGiftTiers: [], Services: [store]);
        var refData = new StubRefData(
            sources: new() { ["GiftedItem"] = [new ItemSource("NpcGift", "NPC_Marna", null)] },
            npcs: new() { ["NPC_Marna"] = npc });

        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("Gifted Item", null, "GiftedItem", []), refData);

        vm.Sources.Single().Requirement.Should().BeNull();
    }

    [Fact]
    public void Monster_source_resolves_area_from_AreaCatalog_when_context_is_known()
    {
        var refData = new StubRefData(
            sources: new()
            {
                ["DragonScale"] = [new ItemSource("Monster", null, "AreaGazluk")],
            },
            areas: new() { ["AreaGazluk"] = new AreaEntry("AreaGazluk", "Gazluk Plateau", "Gazluk") });

        var input = new IngredientSourcesInput("Dragon Scale", null, "DragonScale", []);
        var vm = IngredientSourcesViewModel.Build(input, refData);

        var source = vm.Sources.Single();
        source.Kind.Should().Be("Monster");
        source.AreaFriendlyName.Should().Be("Gazluk Plateau");
    }

    [Fact]
    public void Single_item_with_no_catalogued_sources_shows_placeholder()
    {
        var input = new IngredientSourcesInput("Mystery", null, "MysteryItem", []);
        var vm = IngredientSourcesViewModel.Build(input, EmptyRefData.Instance);

        vm.Sources.Should().BeEmpty();
        vm.SourcesPlaceholder.Should().Contain("No vendor");
    }

    private sealed class StubRefData : IReferenceDataService
    {
        public StubRefData(
            Dictionary<string, IReadOnlyList<ItemSource>>? sources = null,
            Dictionary<string, NpcEntry>? npcs = null,
            Dictionary<string, AreaEntry>? areas = null)
        {
            ItemSources = sources ?? new Dictionary<string, IReadOnlyList<ItemSource>>();
            Npcs = npcs ?? new Dictionary<string, NpcEntry>();
            Areas = areas ?? new Dictionary<string, AreaEntry>();
        }

        public IReadOnlyList<string> Keys => [];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>();
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; }
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; }
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    private static class EmptyRefData
    {
        public static readonly StubRefData Instance = new();
    }
}
