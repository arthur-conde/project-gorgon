using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class ItemsKindTargetTests
{
    [Fact]
    public void Kind_IsItem()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Item);
    }

    [Fact]
    public void TabIndex_IsZero()
    {
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(0);
    }

    [Fact]
    public void TrySelectByInternalName_KnownItem_SelectsOnTabVm_ReturnsTrue()
    {
        var item = new Item { Id = 5010, InternalName = "Tomato", Name = "Tomato" };
        var (target, vm, _) = BuildTarget(item);

        var ok = target.TrySelectByInternalName("Tomato");

        ok.Should().BeTrue();
        vm.SelectedItem.Should().Be(item);
    }

    [Fact]
    public void TrySelectByInternalName_UnknownItem_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.SelectedItem.Should().BeNull();  // precondition

        target.TrySelectByInternalName("DoesNotExist").Should().BeFalse();
        vm.SelectedItem.Should().BeNull();
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsResidualQueryText_SoTargetItemIsVisible()
    {
        // Direct link to an item must clear any leftover filter — otherwise the target
        // row might be filtered out of the visible ListBox and the selection would land
        // on an invisible row.
        var item = new Item { Id = 5010, InternalName = "Tomato", Name = "Tomato" };
        var (target, vm, _) = BuildTarget(item);
        vm.QueryText = "Name STARTSWITH 'XYZ'";

        var ok = target.TrySelectByInternalName("Tomato");

        ok.Should().BeTrue();
        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedItem.Should().Be(item);
    }

    private static (ItemsKindTarget Target, ItemsTabViewModel Vm, FakeReferenceData RefData) BuildTarget(
        params Item[] items)
    {
        var refData = new FakeReferenceData();
        foreach (var item in items)
            refData.AddItem(item);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new ItemsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData));
        var target = new ItemsKindTarget(vm);
        return (target, vm, refData);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<long, Item> _items = new();
        private readonly Dictionary<string, Item> _byName = new(StringComparer.Ordinal);

        public void AddItem(Item item)
        {
            _items[item.Id] = item;
            if (item.InternalName is not null) _byName[item.InternalName] = item;
        }

        public IReadOnlyList<string> Keys => Array.Empty<string>();
        public IReadOnlyDictionary<long, Item> Items => _items;
        public IReadOnlyDictionary<string, Item> ItemsByInternalName => _byName;
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
