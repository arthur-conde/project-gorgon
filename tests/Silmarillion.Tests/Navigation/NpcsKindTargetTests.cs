using FluentAssertions;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Silmarillion.Navigation;
using Silmarillion.ViewModels;
using Xunit;

namespace Silmarillion.Tests.Navigation;

public sealed class NpcsKindTargetTests
{
    [Fact]
    public void Kind_IsNpc()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Npc);
    }

    [Fact]
    public void TabIndex_IsTwo()
    {
        // Items=0, Recipes=1, NPCs=2 — bucket B's first tab slots in after the v1 pair.
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(2);
    }

    [Fact]
    public void TrySelectByInternalName_KnownNpc_SelectsOnTabVm_ReturnsTrue()
    {
        var (target, vm, _) = BuildTarget(("NPC_Joeh", new Npc { Name = "Joeh" }));

        var ok = target.TrySelectByInternalName("NPC_Joeh");

        ok.Should().BeTrue();
        vm.SelectedRow.Should().NotBeNull();
        vm.SelectedRow!.InternalName.Should().Be("NPC_Joeh");
    }

    [Fact]
    public void TrySelectByInternalName_UnknownNpc_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.SelectedRow.Should().BeNull(); // precondition

        target.TrySelectByInternalName("NPC_DoesNotExist").Should().BeFalse();
        vm.SelectedRow.Should().BeNull();
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsResidualQueryText_SoTargetRowIsVisible()
    {
        var (target, vm, _) = BuildTarget(("NPC_Joeh", new Npc { Name = "Joeh" }));
        vm.QueryText = "ServiceTypes CONTAINS 'Store'";

        var ok = target.TrySelectByInternalName("NPC_Joeh");

        ok.Should().BeTrue();
        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedRow!.InternalName.Should().Be("NPC_Joeh");
    }

    private static (NpcsKindTarget Target, NpcsTabViewModel Vm, FakeReferenceData RefData) BuildTarget(
        params (string Key, Npc Npc)[] npcs)
    {
        var refData = new FakeReferenceData();
        foreach (var (key, npc) in npcs)
            refData.AddNpc(key, npc);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = new NpcsTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData));
        var target = new NpcsKindTarget(vm);
        return (target, vm, refData);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, Npc> _byKey = new(StringComparer.Ordinal);

        public void AddNpc(string key, Npc npc) => _byKey[key] = npc;

        public IReadOnlyList<string> Keys => Array.Empty<string>();
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal);
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => _byKey;
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
