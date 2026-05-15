using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

/// <summary>
/// #327: the single-keyword Items filter pivot restored after #326 retired the
/// double-duty <see cref="EntityKind.ItemKeyword"/> for its fan-out use. This is the
/// symmetric Items-side twin of <see cref="EffectKeywordKindTarget"/> — a 1:1 pivot
/// (one tag → Items tab filtered by <c>Keywords CONTAINS</c>), NOT the retired #270
/// recipe-slot fan-out kind (no '+'-joined composite, no query re-derivation).
/// </summary>
public sealed class ItemByKeywordKindTargetTests
{
    [Fact]
    public void Kind_IsItemByKeyword()
    {
        var (target, _) = Build();
        target.Kind.Should().Be(EntityKind.ItemByKeyword);
    }

    [Fact]
    public void Kind_IsNotTheRetiredItemKeywordFanOutKind()
    {
        // Lock the deliberate naming split: the retired #270 ItemKeyword fan-out enum
        // value must NOT be resurrected. ItemByKeyword is a distinct, new kind so the
        // SilmarillionDeepLinkHandler "ItemKeyword" route-rejection lock stays valid.
        Enum.GetNames<EntityKind>().Should().NotContain("ItemKeyword");
        Enum.GetNames<EntityKind>().Should().Contain("ItemByKeyword");
    }

    [Fact]
    public void TabIndex_IsZero()
    {
        var (target, _) = Build();
        target.TabIndex.Should().Be(0); // Items tab
    }

    [Fact]
    public void TryOpenInWindow_ReturnsFalse()
    {
        // Filter-pivot navigation flips the tab + query; it never opens a window, so it
        // pushes no window navigator history (mirrors EffectKeywordKindTarget / #229).
        var (target, _) = Build();
        target.TryOpenInWindow().Should().BeFalse();
    }

    [Fact]
    public void TrySelectByInternalName_SetsKeywordsContainsFilter_OnItemsTab()
    {
        var (target, vm) = Build();

        var ok = target.TrySelectByInternalName("Sword");

        ok.Should().BeTrue();
        vm.QueryText.Should().Be("Keywords CONTAINS \"Sword\"");
    }

    [Fact]
    public void TrySelectByInternalName_EscapesEmbeddedQuotes()
    {
        var (target, vm) = Build();

        target.TrySelectByInternalName("Odd\"Tag").Should().BeTrue();

        vm.QueryText.Should().Be("Keywords CONTAINS \"Odd\\\"Tag\"");
    }

    [Fact]
    public void TrySelectByInternalName_EmptyKeyword_ReturnsFalse_VmUnchanged()
    {
        var (target, vm) = Build();

        target.TrySelectByInternalName("").Should().BeFalse();
        vm.QueryText.Should().BeEmpty();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsPriorSelection_SoFilteredListHasNoStaleRow()
    {
        var item = new Item { Id = 1, InternalName = "Longsword", Name = "Longsword" };
        var (target, vm) = Build(item);
        vm.SelectedItem = item;

        target.TrySelectByInternalName("Sword").Should().BeTrue();

        vm.SelectedItem.Should().BeNull(because: "filter-only navigation must drop any stale detail selection");
        vm.QueryText.Should().Be("Keywords CONTAINS \"Sword\"");
    }

    private static (ItemByKeywordKindTarget Target, ItemsTabViewModel Vm) Build(params Item[] items)
    {
        var refData = new FakeReferenceData();
        foreach (var item in items) refData.AddItem(item);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new ItemsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData));
        return (new ItemByKeywordKindTarget(vm), vm);
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
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
