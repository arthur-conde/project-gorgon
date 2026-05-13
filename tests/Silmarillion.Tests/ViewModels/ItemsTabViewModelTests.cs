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

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, Item> ItemsByName { get; } = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> KeywordsInRecipeSlots { get; init; } = [];

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items => ItemsByName.Values.ToDictionary(i => i.Id);
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => ItemsByName;
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

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
