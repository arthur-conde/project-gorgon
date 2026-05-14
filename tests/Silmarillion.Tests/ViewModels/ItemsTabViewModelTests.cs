using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.ViewModels;

public sealed class ItemsTabViewModelTests
{
    [Fact]
    public void AllItems_PopulatedFromReferenceData_OrderedByName()
    {
        var refData = new StubReferenceData
        {
            ItemsByName =
            {
                ["Tomato"] = new Item { Id = 1, InternalName = "Tomato", Name = "Tomato", IconId = 1 },
                ["Apple"] = new Item { Id = 2, InternalName = "Apple", Name = "Apple", IconId = 2 },
            },
        };

        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.AllItems.Should().HaveCount(2);
        vm.AllItems.Select(i => i.Name).Should().Equal("Apple", "Tomato");
    }

    [Fact]
    public void SelectingItem_BuildsDetailViewModel()
    {
        var item = new Item { Id = 1, InternalName = "Tomato", Name = "Tomato", IconId = 1 };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["Tomato"] = item },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedItem = item;

        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.Item.Should().Be(item);
    }

    [Fact]
    public void DeselectingItem_ClearsDetailViewModel()
    {
        var item = new Item { Id = 1, InternalName = "Tomato", Name = "Tomato" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["Tomato"] = item },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedItem = item;
        vm.SelectedItem = null;

        vm.DetailViewModel.Should().BeNull();
    }

    [Fact]
    public void BuildCrossLinkContext_emits_keyword_chips_for_intersection_of_item_Keywords_and_KeywordsUsedInRecipeSlots()
    {
        var tourmaline = new Item
        {
            Id = 99,
            InternalName = "MassiveTourmaline",
            Name = "Massive Tourmaline",
            IconId = 0,
            Keywords = [new ItemKeyword("Crystal", 0), new ItemKeyword("Bogus", 0)],
        };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MassiveTourmaline"] = tourmaline },
            KeywordsInRecipeSlots = new HashSet<string>(StringComparer.Ordinal) { "Crystal" },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedItem = tourmaline;

        vm.DetailViewModel.Should().NotBeNull();
        var chips = vm.DetailViewModel!.ConsumedAsKeywordIn;
        chips.Should().ContainSingle();
        chips.Single().DisplayName.Should().Be("Crystal");
        chips.Single().IconId.Should().Be(0);
        chips.Single().Reference.Should().Be(EntityRef.RecipeIngredientKeyword("Crystal"));
    }

    [Fact]
    public void Keyword_chip_uses_KeywordDisplayNames_when_present()
    {
        var item = new Item
        {
            Id = 99,
            InternalName = "ShinyArmor",
            Name = "Shiny Armor",
            Keywords = [new ItemKeyword("MetalArmor", 0)],
        };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["ShinyArmor"] = item },
            KeywordsInRecipeSlots = new HashSet<string>(StringComparer.Ordinal) { "MetalArmor" },
            KeywordDisplays = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MetalArmor"] = "Metal Armor",  // friendly form sourced from a recipe slot
            },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedItem = item;

        vm.DetailViewModel!.ConsumedAsKeywordIn.Single().DisplayName
            .Should().Be("Metal Armor", because: "KeywordDisplayNames lookup wins over the raw tag");
    }

    [Fact]
    public void UsedIn_belowCap_emitsAllChips_andNoMoreRecipesChip()
    {
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient =
            {
                ["MetalSlab1"] = MakeRecipeList(count: 5),
            },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;

        vm.DetailViewModel!.ConsumedByRecipes.Should().HaveCount(5);
        vm.DetailViewModel.MoreRecipesChip.Should().BeNull();
    }

    [Fact]
    public void UsedIn_aboveCap_capsChips_andEmitsMoreRecipesPill()
    {
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab", IconId = 4242 };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient =
            {
                // The MetalSlab1 case from the issue (~69 recipes).
                ["MetalSlab1"] = MakeRecipeList(count: 69),
            },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;

        vm.DetailViewModel!.ConsumedByRecipes.Should().HaveCount(12,
            because: "cap 12 means the first dozen show as chips and the rest collapse behind the pill");

        var pill = vm.DetailViewModel.MoreRecipesChip;
        pill.Should().NotBeNull();
        pill!.DisplayName.Should().Be("+57 more →");
        pill.IconId.Should().Be(4242, because: "pill carries the item's own icon for visual continuity");
        pill.Reference.Should().Be(EntityRef.RecipeIngredientItem("MetalSlab1"));
    }

    [Fact]
    public void UsedIn_capZero_collapsesEverythingIntoPill()
    {
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient = { ["MetalSlab1"] = MakeRecipeList(count: 3) },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()),
            new SilmarillionSettings { UsedInChipCap = 0 });

        vm.SelectedItem = item;

        vm.DetailViewModel!.ConsumedByRecipes.Should().BeEmpty();
        vm.DetailViewModel.MoreRecipesChip.Should().NotBeNull();
        vm.DetailViewModel.MoreRecipesChip!.DisplayName.Should().Be("+3 more →");
    }

    [Fact]
    public void UsedIn_capExactlyMatchesCount_noOverflowPill()
    {
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient = { ["MetalSlab1"] = MakeRecipeList(count: 12) },
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()),
            new SilmarillionSettings { UsedInChipCap = 12 });

        vm.SelectedItem = item;

        vm.DetailViewModel!.ConsumedByRecipes.Should().HaveCount(12);
        vm.DetailViewModel.MoreRecipesChip.Should().BeNull(
            because: "the pill appears only when count exceeds the cap, not when they're equal");
    }

    [Fact]
    public void UsedInChipCap_change_liveRebuilds_DetailViewModel()
    {
        // The user drags the slider; the open detail view should re-cap on the spot. We
        // verify by changing the cap and checking that ConsumedByRecipes / MoreRecipesChip
        // reflect the new value.
        var item = new Item { Id = 1, InternalName = "MetalSlab1", Name = "Metal Slab" };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["MetalSlab1"] = item },
            RecipesByIngredient = { ["MetalSlab1"] = MakeRecipeList(count: 20) },
        };
        var settings = new SilmarillionSettings { UsedInChipCap = 12 };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()), settings);
        vm.SelectedItem = item;

        var before = vm.DetailViewModel!;
        before.ConsumedByRecipes.Should().HaveCount(12);
        before.MoreRecipesChip.Should().NotBeNull();

        settings.UsedInChipCap = 25;

        var after = vm.DetailViewModel!;
        after.Should().NotBeSameAs(before, because: "live-update rebuilds the detail VM on slider drag");
        after.ConsumedByRecipes.Should().HaveCount(20);
        after.MoreRecipesChip.Should().BeNull();
    }

    private static IReadOnlyList<Recipe> MakeRecipeList(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Recipe { Key = $"recipe_{i:D3}", InternalName = $"Recipe{i:D3}", Name = $"Recipe {i:D3}" })
            .ToList();

    [Fact]
    public void Keyword_chip_falls_back_to_CamelCaseSplit_when_no_friendly_display_name()
    {
        // Mirrors the GreenCrystal case: no recipe carries a friendly singleton Desc for the keyword,
        // so the map has no entry. CamelCaseSplit on the raw tag is the visible result.
        var item = new Item
        {
            Id = 99,
            InternalName = "Tourmaline",
            Name = "Tourmaline",
            Keywords = [new ItemKeyword("GreenCrystal", 0)],
        };
        var refData = new StubReferenceData
        {
            ItemsByName = { ["Tourmaline"] = item },
            KeywordsInRecipeSlots = new HashSet<string>(StringComparer.Ordinal) { "GreenCrystal" },
            // KeywordDisplays intentionally empty — no friendly display name available.
        };
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()));

        vm.SelectedItem = item;

        vm.DetailViewModel!.ConsumedAsKeywordIn.Single().DisplayName
            .Should().Be("Green Crystal",
                because: "CamelCaseSplit splits PascalCase tokens at the camel-hump");
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, Item> ItemsByName { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<Recipe>> RecipesByIngredient { get; } = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> KeywordsInRecipeSlots { get; init; } = [];
        public IReadOnlyDictionary<string, string> KeywordDisplays { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items => ItemsByName.Values.ToDictionary(i => i.Id);
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => ItemsByName;
        public IReadOnlyDictionary<string, IReadOnlyList<Recipe>> RecipesByIngredientItem => RecipesByIngredient;
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public IReadOnlyCollection<string> KeywordsUsedInRecipeSlots => KeywordsInRecipeSlots;
        public IReadOnlyDictionary<string, string> KeywordDisplayNames => KeywordDisplays;

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
