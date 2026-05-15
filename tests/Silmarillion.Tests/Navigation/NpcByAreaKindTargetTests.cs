using FluentAssertions;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;
using PocoEffect = Mithril.Reference.Models.Effects.Effect;

namespace Silmarillion.Tests.Navigation;

public sealed class NpcByAreaKindTargetTests
{
    [Fact]
    public void Kind_IsNpcByArea()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.NpcByArea);
    }

    [Fact]
    public void TabIndex_IsTwo()
    {
        // NPCs tab is TabOrder 2.
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(2);
    }

    [Fact]
    public void TrySelectByInternalName_SetsExactMatchQuery_OnNpcsTabVm()
    {
        var (target, vm, _) = BuildTarget();

        var ok = target.TrySelectByInternalName("AreaSerbule");

        ok.Should().BeTrue();
        vm.QueryText.Should().Be("AreaName = \"AreaSerbule\"",
            "exact match on the envelope key — friendly names overlap (Serbule vs Serbule Hills).");
        vm.SelectedRow.Should().BeNull("filter actions clear any prior row selection.");
    }

    [Fact]
    public void TrySelectByInternalName_EmptyPayload_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.QueryText = "preexisting filter";

        target.TrySelectByInternalName("").Should().BeFalse();
        vm.QueryText.Should().Be("preexisting filter",
            "empty payload should not stomp on prior query text.");
    }

    [Fact]
    public void TrySelectByInternalName_EscapesEmbeddedQuotes()
    {
        // Defensive: area keys never carry quotes in practice, but the escape should still apply.
        var (target, vm, _) = BuildTarget();

        target.TrySelectByInternalName("Area\"With\"Quotes").Should().BeTrue();
        vm.QueryText.Should().Be("AreaName = \"Area\\\"With\\\"Quotes\"");
    }

    [Fact]
    public void TryOpenInWindow_AlwaysReturnsFalse()
    {
        // Synthetic kind targets have nothing to open in isolation.
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    private static (NpcByAreaKindTarget Target, NpcsTabViewModel Vm, FakeReferenceData RefData) BuildTarget()
    {
        var refData = new FakeReferenceData();
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new NpcsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData));
        var target = new NpcByAreaKindTarget(vm);
        return (target, vm, refData);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
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
