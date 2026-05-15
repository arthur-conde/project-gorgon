using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;
using LorebookPoco = Mithril.Reference.Models.Misc.Lorebook;

namespace Silmarillion.Tests.Navigation;

public sealed class LorebooksKindTargetTests
{
    [Fact]
    public void Kind_IsLorebook()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Lorebook);
    }

    [Fact]
    public void TabIndex_IsSeven()
    {
        // Items=0 … Areas=6, Lorebooks=7 — eighth tab. Must match TabOrder.
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(7);
    }

    [Fact]
    public void TrySelectByInternalName_KnownBook_SelectsOnTabVm_ReturnsTrue()
    {
        var (target, vm, _) = BuildTarget(
            ("Book_101", "TheWastedWishes", "The Wasted Wishes"));

        var ok = target.TrySelectByInternalName("TheWastedWishes");

        ok.Should().BeTrue();
        vm.SelectedLorebook.Should().NotBeNull();
        vm.SelectedLorebook!.InternalName.Should().Be("TheWastedWishes");
        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("The Wasted Wishes");
    }

    [Fact]
    public void TrySelectByInternalName_EnvelopeKey_DoesNotMatch_ReturnsFalse()
    {
        // The selection contract is the bare InternalName, NOT the "Book_N" envelope key.
        var (target, vm, _) = BuildTarget(
            ("Book_101", "TheWastedWishes", "The Wasted Wishes"));

        target.TrySelectByInternalName("Book_101").Should().BeFalse();
        vm.SelectedLorebook.Should().BeNull();
    }

    [Fact]
    public void TrySelectByInternalName_UnknownBook_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.SelectedLorebook.Should().BeNull();

        target.TrySelectByInternalName("DoesNotExist").Should().BeFalse();
        vm.SelectedLorebook.Should().BeNull();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsResidualQueryText()
    {
        var (target, vm, _) = BuildTarget(
            ("Book_101", "TheWastedWishes", "The Wasted Wishes"));
        vm.QueryText = "CategoryKey = 'Gods'";

        target.TrySelectByInternalName("TheWastedWishes").Should().BeTrue();

        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedLorebook!.InternalName.Should().Be("TheWastedWishes");
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    private static (LorebooksKindTarget Target, LorebooksTabViewModel Vm, FakeReferenceData RefData)
        BuildTarget(params (string EnvelopeKey, string InternalName, string Title)[] books)
    {
        var refData = new FakeReferenceData();
        foreach (var (key, name, title) in books)
            refData.AddLorebook(key, new LorebookPoco
            {
                Category = "Stories",
                InternalName = name,
                Title = title,
                Text = "<h1>" + title + "</h1>body",
            });
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var settings = new SilmarillionSettings();
        var vm = new LorebooksTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData), settings);
        var target = new LorebooksKindTarget(vm);
        return (target, vm, refData);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, LorebookPoco> _lorebooks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LorebookPoco> _byInternalName = new(StringComparer.Ordinal);

        public void AddLorebook(string envelopeKey, LorebookPoco book)
        {
            _lorebooks[envelopeKey] = book;
            if (book.InternalName is not null) _byInternalName[book.InternalName] = book;
        }

        public IReadOnlyDictionary<string, LorebookPoco> Lorebooks => _lorebooks;
        public IReadOnlyDictionary<string, LorebookPoco> LorebooksByInternalName => _byInternalName;

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName { get; } = new Dictionary<string, Npc>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();

        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
