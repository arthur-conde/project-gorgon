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

        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator());

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
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator());

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
        var vm = new ItemsTabViewModel(refData, new SilmarillionReferenceNavigator());

        vm.SelectedItem = item;
        vm.SelectedItem = null;

        vm.DetailViewModel.Should().BeNull();
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, Item> ItemsByName { get; } = new(StringComparer.Ordinal);

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

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
