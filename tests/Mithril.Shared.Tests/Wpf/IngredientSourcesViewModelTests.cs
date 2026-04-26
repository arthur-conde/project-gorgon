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
    public void Recipe_source_renders_label_and_skill_gate()
    {
        var recipe = new RecipeEntry(
            Key: "recipe_101",
            Name: "Tin Bar",
            InternalName: "TinBar",
            IconId: 0,
            Skill: "Smelting",
            SkillLevelReq: 5,
            RewardSkill: "Smelting",
            RewardSkillXp: 10,
            RewardSkillXpFirstTime: 0,
            RewardSkillXpDropOffLevel: null,
            RewardSkillXpDropOffPct: null,
            RewardSkillXpDropOffRate: null,
            Ingredients: [],
            ResultItems: []);

        var refData = new StubRefData(
            sources: new() { ["TinBar"] = [new ItemSource("Recipe", null, "TinBar")] },
            recipesByInternalName: new() { ["TinBar"] = recipe });

        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("Tin Bar", null, "TinBar", []), refData);

        var source = vm.Sources.Single();
        source.Kind.Should().Be("Crafted");
        source.Label.Should().Be("Tin Bar");
        source.Requirement.Should().Be("Requires Smelting 5");
        source.Detail.Should().BeNull();
    }

    [Fact]
    public void Recipe_source_with_PrereqRecipe_surfaces_it_as_Detail()
    {
        var recipe = new RecipeEntry(
            "recipe_500", "Advanced Bread", "AdvancedBread", 0,
            "Cooking", 30, "Cooking", 50, 0, null, null, null,
            Ingredients: [], ResultItems: [], PrereqRecipe: "BasicBread");

        var refData = new StubRefData(
            sources: new() { ["AdvancedBread"] = [new ItemSource("Recipe", null, "AdvancedBread")] },
            recipesByInternalName: new() { ["AdvancedBread"] = recipe });

        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("Advanced Bread", null, "AdvancedBread", []), refData);

        var source = vm.Sources.Single();
        source.Requirement.Should().Be("Requires Cooking 30");
        source.Detail.Should().Be("prereq: BasicBread");
    }

    [Fact]
    public void Single_skill_use_gate_renders_in_title_bar()
    {
        var item = new ItemEntry(
            Id: 1, Name: "Cabbage Leafling", InternalName: "CabbageLeafling",
            MaxStackSize: 50, IconId: 0, Keywords: [],
            SkillReqs: new Dictionary<string, int> { ["Gardening"] = 10 });
        var refData = new StubRefData(
            itemsByInternalName: new() { ["CabbageLeafling"] = item });

        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("Cabbage Leafling", null, "CabbageLeafling", []), refData);

        vm.UseRequirement.Should().Be("Requires Gardening 10 to use");
    }

    [Fact]
    public void Multi_skill_use_gate_renders_comma_joined_alphabetical()
    {
        var item = new ItemEntry(
            Id: 2, Name: "Wolfen Speed Potion", InternalName: "WolfenSpeed",
            MaxStackSize: 50, IconId: 0, Keywords: [],
            SkillReqs: new Dictionary<string, int> { ["Werewolf"] = 35, ["Alchemy"] = 35 });
        var refData = new StubRefData(
            itemsByInternalName: new() { ["WolfenSpeed"] = item });

        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("Wolfen Speed Potion", null, "WolfenSpeed", []), refData);

        // Alphabetical ordering keeps the rendering stable across runs.
        vm.UseRequirement.Should().Be("Requires Alchemy 35, Werewolf 35 to use");
    }

    [Fact]
    public void Item_with_no_skill_requirements_has_null_UseRequirement()
    {
        var item = new ItemEntry(1, "Plain Item", "Plain", 50, 0, []);
        var refData = new StubRefData(itemsByInternalName: new() { ["Plain"] = item });

        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("Plain", null, "Plain", []), refData);

        vm.UseRequirement.Should().BeNull();
    }

    [Fact]
    public void Keyword_row_skips_UseRequirement()
    {
        // A keyword row has no single item to gate, even if some matching items have SkillReqs.
        var refData = new StubRefData();
        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("Auxiliary Crystal", "any Crystal", ItemInternalName: null, OnHand: []),
            refData);

        vm.UseRequirement.Should().BeNull();
    }

    [Fact]
    public void Recipe_source_with_unresolved_context_falls_back_gracefully()
    {
        var refData = new StubRefData(
            sources: new() { ["X"] = [new ItemSource("Recipe", null, "UnknownRecipe")] });

        var vm = IngredientSourcesViewModel.Build(
            new IngredientSourcesInput("X", null, "X", []), refData);

        var source = vm.Sources.Single();
        source.Kind.Should().Be("Recipe");
        source.Label.Should().Be("Crafted");
        source.Requirement.Should().BeNull();
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
            Dictionary<string, AreaEntry>? areas = null,
            Dictionary<string, RecipeEntry>? recipesByInternalName = null,
            Dictionary<string, ItemEntry>? itemsByInternalName = null)
        {
            ItemSources = sources ?? new Dictionary<string, IReadOnlyList<ItemSource>>();
            Npcs = npcs ?? new Dictionary<string, NpcEntry>();
            Areas = areas ?? new Dictionary<string, AreaEntry>();
            RecipesByInternalName = recipesByInternalName ?? new Dictionary<string, RecipeEntry>();
            ItemsByInternalName = itemsByInternalName ?? new Dictionary<string, ItemEntry>();
        }

        public IReadOnlyList<string> Keys => [];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>();
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; }
        public ItemKeywordIndex KeywordIndex { get; } = ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; }
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
