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

public sealed class AreasKindTargetTests
{
    [Fact]
    public void Kind_IsArea()
    {
        var (target, _, _) = BuildTarget();
        target.Kind.Should().Be(EntityKind.Area);
    }

    [Fact]
    public void TabIndex_IsSix()
    {
        // Items=0, Recipes=1, NPCs=2, Quests=3, Abilities=4, Effects=5, Areas=6 — seventh tab.
        var (target, _, _) = BuildTarget();
        target.TabIndex.Should().Be(6);
    }

    [Fact]
    public void TrySelectByInternalName_KnownArea_SelectsOnTabVm_ReturnsTrue()
    {
        var (target, vm, _) = BuildTarget(
            new AreaEntry("AreaSerbule", "Serbule", "Serbule"));

        var ok = target.TrySelectByInternalName("AreaSerbule");

        ok.Should().BeTrue();
        vm.SelectedArea.Should().NotBeNull();
        vm.SelectedArea!.Key.Should().Be("AreaSerbule");
        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Serbule");
    }

    [Fact]
    public void TrySelectByInternalName_UnknownArea_ReturnsFalse_VmUnchanged()
    {
        var (target, vm, _) = BuildTarget();
        vm.SelectedArea.Should().BeNull();

        target.TrySelectByInternalName("AreaDoesNotExist").Should().BeFalse();
        vm.SelectedArea.Should().BeNull();
    }

    [Fact]
    public void TrySelectByInternalName_ClearsResidualQueryText_SoTargetRowIsVisible()
    {
        var (target, vm, _) = BuildTarget(
            new AreaEntry("AreaSerbule", "Serbule", "Serbule"));
        vm.QueryText = "FriendlyName CONTAINS 'Eltibule'";

        var ok = target.TrySelectByInternalName("AreaSerbule");

        ok.Should().BeTrue();
        vm.QueryText.Should().BeEmpty(because: "the kind target clears any prior filter before selecting");
        vm.SelectedArea!.Key.Should().Be("AreaSerbule");
    }

    [Fact]
    public void TryOpenInWindow_NoDetailSelected_ReturnsFalse()
    {
        var (target, _, _) = BuildTarget();
        target.TryOpenInWindow().Should().BeFalse();
    }

    private static (AreasKindTarget Target, AreasTabViewModel Vm, FakeReferenceData RefData) BuildTarget(
        params AreaEntry[] areas)
    {
        var refData = new FakeReferenceData();
        foreach (var area in areas) refData.AddArea(area);
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var settings = new SilmarillionSettings();
        var vm = new AreasTabViewModel(refData, nav, new ReferenceDataEntityNameResolver(refData), settings);
        var target = new AreasKindTarget(vm);
        return (target, vm, refData);
    }

    private sealed class FakeReferenceData : IReferenceDataService
    {
        private readonly Dictionary<string, AreaEntry> _areas = new(StringComparer.Ordinal);

        public void AddArea(AreaEntry area) => _areas[area.Key] = area;

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
        public IReadOnlyDictionary<string, AreaEntry> Areas => _areas;
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
