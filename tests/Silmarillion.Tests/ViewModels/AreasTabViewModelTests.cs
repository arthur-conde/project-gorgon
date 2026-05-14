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
using PocoLandmark = Mithril.Reference.Models.Misc.Landmark;

namespace Silmarillion.Tests.ViewModels;

public sealed class AreasTabViewModelTests
{
    [Fact]
    public void AllAreas_PopulatedFromReferenceData_OrderedByFriendlyName()
    {
        var refData = new StubReferenceData
        {
            AreasMap =
            {
                ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "Eltibule"),
                ["AreaSerbule"] = new AreaEntry("AreaSerbule", "Serbule", "Serbule"),
                ["AreaAnagoge"] = new AreaEntry("AreaAnagoge", "Anagoge Island", "Anagoge"),
            },
        };

        var vm = BuildVm(refData);

        vm.AllAreas.Should().HaveCount(3);
        vm.AllAreas.Select(a => a.FriendlyName).Should().Equal("Anagoge Island", "Eltibule", "Serbule");
    }

    [Fact]
    public void SchemaSnapshot_IncludesQueryableColumns()
    {
        var names = AreasTabViewModel.SchemaSnapshot.Select(c => c.Name).ToList();
        names.Should().Contain("Key");
        names.Should().Contain("FriendlyName");
        names.Should().Contain("ShortFriendlyName");
    }

    [Fact]
    public void Selection_BuildsDetailViewModel_OnRowSelect()
    {
        var refData = new StubReferenceData
        {
            AreasMap = { ["AreaSerbule"] = new AreaEntry("AreaSerbule", "Serbule", "Serbule") },
        };
        var vm = BuildVm(refData);

        vm.SelectedArea = vm.AllAreas.Single();

        vm.DetailViewModel.Should().NotBeNull();
        vm.DetailViewModel!.DisplayName.Should().Be("Serbule");
    }

    [Fact]
    public void Selection_ClearsDetailViewModel_OnDeselect()
    {
        var refData = new StubReferenceData
        {
            AreasMap = { ["AreaSerbule"] = new AreaEntry("AreaSerbule", "Serbule", "Serbule") },
        };
        var vm = BuildVm(refData);
        vm.SelectedArea = vm.AllAreas.Single();
        vm.DetailViewModel.Should().NotBeNull();

        vm.SelectedArea = null;

        vm.DetailViewModel.Should().BeNull();
    }

    [Fact]
    public void FileUpdated_Areas_RebuildsList_PreservingSelection()
    {
        var refData = new StubReferenceData
        {
            AreasMap = { ["AreaSerbule"] = new AreaEntry("AreaSerbule", "Serbule", "Serbule") },
        };
        var vm = BuildVm(refData);
        vm.SelectedArea = vm.AllAreas.Single();

        refData.AreasMap["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "Eltibule");
        refData.RaiseFileUpdated("areas");

        vm.AllAreas.Should().HaveCount(2);
        vm.SelectedArea.Should().NotBeNull(because: "selection should be preserved across a refresh");
        vm.SelectedArea!.Key.Should().Be("AreaSerbule");
    }

    [Fact]
    public void FileUpdated_Npcs_RebuildsDetailViewModel_PreservingSelection()
    {
        var refData = new StubReferenceData
        {
            AreasMap = { ["AreaSerbule"] = new AreaEntry("AreaSerbule", "Serbule", "Serbule") },
            NpcsByInternalNameMap =
            {
                ["NPC_Joeh"] = new Npc { Name = "Joeh", AreaName = "AreaSerbule" },
            },
            NpcsMap =
            {
                ["NPC_Joeh"] = new NpcEntry("NPC_Joeh", "Joeh", "Serbule", [], [], []),
            },
            NpcsByAreaMap =
            {
                ["AreaSerbule"] = new[] { new NpcEntry("NPC_Joeh", "Joeh", "Serbule", [], [], []) },
            },
        };
        var vm = BuildVm(refData);
        vm.SelectedArea = vm.AllAreas.Single();
        vm.DetailViewModel!.NpcChips.Should().ContainSingle();

        refData.NpcsByAreaMap["AreaSerbule"] = new[]
        {
            new NpcEntry("NPC_Joeh", "Joeh", "Serbule", [], [], []),
            new NpcEntry("NPC_Marna", "Marna", "Serbule", [], [], []),
        };
        refData.RaiseFileUpdated("npcs");

        vm.SelectedArea.Should().NotBeNull(because: "an NPC refresh shouldn't drop the area selection");
        vm.SelectedArea!.Key.Should().Be("AreaSerbule");
        vm.DetailViewModel!.NpcChips.Should().HaveCount(2,
            because: "the detail VM should rebuild against the fresh NpcsByArea snapshot");
    }

    [Fact]
    public void FileUpdated_Landmarks_RebuildsDetailViewModel()
    {
        var refData = new StubReferenceData
        {
            AreasMap = { ["AreaSerbule"] = new AreaEntry("AreaSerbule", "Serbule", "Serbule") },
        };
        var vm = BuildVm(refData);
        vm.SelectedArea = vm.AllAreas.Single();
        vm.DetailViewModel!.LandmarkGroups.Should().BeEmpty();

        refData.LandmarksMap["AreaSerbule"] = new[]
        {
            new PocoLandmark { Name = "P1", Type = "Portal", Desc = "Exit", Loc = "x:0 y:0 z:0" },
        };
        refData.RaiseFileUpdated("landmarks");

        vm.DetailViewModel!.LandmarkGroups.Should().ContainSingle()
            .Which.Type.Should().Be("Portal");
    }

    [Fact]
    public void FileUpdated_IgnoresUnrelatedFiles()
    {
        // areas / npcs / landmarks drive this tab; effects / abilities / etc must not trigger
        // a rebuild or detail-VM regeneration. Sanity test against the file-list — if a new
        // file-name slips into the OnFileUpdated switch, this test surfaces the over-firing.
        var refData = new StubReferenceData
        {
            AreasMap = { ["AreaSerbule"] = new AreaEntry("AreaSerbule", "Serbule", "Serbule") },
        };
        var vm = BuildVm(refData);
        vm.SelectedArea = vm.AllAreas.Single();
        var detailBefore = vm.DetailViewModel;

        refData.RaiseFileUpdated("items");
        refData.RaiseFileUpdated("recipes");
        refData.RaiseFileUpdated("effects");

        vm.DetailViewModel.Should().BeSameAs(detailBefore,
            because: "unrelated file refreshes should not rebuild the detail VM");
    }

    private static AreasTabViewModel BuildVm(StubReferenceData refData) =>
        new AreasTabViewModel(
            refData,
            new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()),
            new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings());

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, AreaEntry> AreasMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<PocoLandmark>> LandmarksMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<NpcEntry>> NpcsByAreaMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, NpcEntry> NpcsMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Npc> NpcsByInternalNameMap { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs => NpcsMap;
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => NpcsByInternalNameMap;
        public IReadOnlyDictionary<string, AreaEntry> Areas => AreasMap;
        public IReadOnlyDictionary<string, IReadOnlyList<PocoLandmark>> Landmarks => LandmarksMap;
        public IReadOnlyDictionary<string, IReadOnlyList<NpcEntry>> NpcsByArea => NpcsByAreaMap;
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
        public event EventHandler<string>? FileUpdated;
        public void RaiseFileUpdated(string fileKey) => FileUpdated?.Invoke(this, fileKey);
    }
}
