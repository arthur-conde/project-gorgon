using CommunityToolkit.Mvvm.Input;
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

public sealed class AreaDetailViewModelTests
{
    [Fact]
    public void Header_DisplayName_FromFriendlyName()
    {
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule Hills", "Serbule"));
        vm.DisplayName.Should().Be("Serbule Hills");
        vm.InternalName.Should().Be("AreaSerbule");
    }

    [Fact]
    public void ShortFriendlyName_NulledWhenEqualToFriendlyName()
    {
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"));
        vm.ShortFriendlyName.Should().BeNull(
            "noise-filtering: redundant subtitle suppressed when identical to the header.");
    }

    [Fact]
    public void ShortFriendlyName_KeptWhenDifferent()
    {
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule Hills", "Serbule"));
        vm.ShortFriendlyName.Should().Be("Serbule");
    }

    [Fact]
    public void NpcChips_SortedByDisplayName()
    {
        var stub = new StubReferenceData
        {
            NpcsByAreaMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new NpcEntry("NPC_Norbert", "Norbert", "Serbule", [], [], []),
                    new NpcEntry("NPC_Marna", "Marna", "Serbule", [], [], []),
                    new NpcEntry("NPC_Joeh", "Joeh", "Serbule", [], [], []),
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        vm.NpcChips.Select(c => c.DisplayName).Should().Equal("Joeh", "Marna", "Norbert");
        vm.NpcOverflowCount.Should().Be(0);
    }

    [Fact]
    public void NpcChips_CappedAtUsedInChipCap_OverflowCountReportsExtras()
    {
        var npcs = Enumerable.Range(1, 30)
            .Select(i => new NpcEntry($"NPC_{i:000}", $"NPC {i:000}", "Serbule", [], [], []))
            .ToArray();
        var stub = new StubReferenceData
        {
            NpcsByAreaMap = { ["AreaSerbule"] = npcs },
        };
        var settings = new SilmarillionSettings { UsedInChipCap = 12 };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub, settings);

        vm.NpcChips.Should().HaveCount(12);
        vm.NpcOverflowCount.Should().Be(18);
    }

    [Fact]
    public void NpcChips_EmptyWhenNoNpcsInArea()
    {
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"));
        vm.NpcChips.Should().BeEmpty();
        vm.NpcOverflowCount.Should().Be(0);
        vm.HasNpcs.Should().BeFalse();
    }

    [Fact]
    public void LandmarkGroups_PartitionByType_OrderedMeditationPillarPortalPlatform()
    {
        // Render order matches gameplay-relevance: pillars first (Combo readout is the
        // most-asked landmark question), then portals (route-finding), then platforms.
        var stub = new StubReferenceData
        {
            LandmarksMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new PocoLandmark { Name = "Portal A", Type = "Portal", Desc = "Exit", Loc = "x:0 y:0 z:0" },
                    new PocoLandmark { Name = "Pillar A", Type = "MeditationPillar", Combo = "4017", Loc = "x:1 y:1 z:1" },
                    new PocoLandmark { Name = "Platform A", Type = "TeleportationPlatform", Loc = "x:2 y:2 z:2" },
                    new PocoLandmark { Name = "Portal B", Type = "Portal", Desc = "Exit", Loc = "x:3 y:3 z:3" },
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        vm.LandmarkGroups.Should().HaveCount(3);
        vm.LandmarkGroups[0].Type.Should().Be("MeditationPillar");
        vm.LandmarkGroups[1].Type.Should().Be("Portal");
        vm.LandmarkGroups[2].Type.Should().Be("TeleportationPlatform");
        vm.LandmarkGroups[1].Rows.Should().HaveCount(2, "two portals in this area");
    }

    [Fact]
    public void LandmarkGroups_EmptyTypesHidden()
    {
        // No platforms in this fixture — Teleportation Platform group should NOT render.
        var stub = new StubReferenceData
        {
            LandmarksMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new PocoLandmark { Name = "Pillar A", Type = "MeditationPillar", Combo = "4017", Loc = "x:1 y:1 z:1" },
                    new PocoLandmark { Name = "Portal A", Type = "Portal", Desc = "Exit", Loc = "x:0 y:0 z:0" },
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        vm.LandmarkGroups.Should().HaveCount(2);
        vm.LandmarkGroups.Select(g => g.Type).Should().BeEquivalentTo(["MeditationPillar", "Portal"]);
    }

    [Fact]
    public void PillarRow_ComboReadoutPopulated()
    {
        var stub = new StubReferenceData
        {
            LandmarksMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new PocoLandmark { Name = "Pillar A", Type = "MeditationPillar", Combo = "4017", Loc = "x:1 y:2 z:3" },
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        var pillar = vm.LandmarkGroups.Single(g => g.Type == "MeditationPillar").Rows.Single()
            .Should().BeOfType<AreaLandmarkPillarRow>().Subject;
        pillar.Combo.Should().Be("4017");
        pillar.ComboDisplay.Should().Be(" · Combo: 4017");
        pillar.LocDisplay.Should().Be(" · x:1 y:2 z:3");
    }

    [Fact]
    public void PortalRow_DescAndLocDisplaysIncludeSeparators()
    {
        var stub = new StubReferenceData
        {
            LandmarksMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new PocoLandmark { Name = "To Eltibule", Type = "Portal", Desc = "Return to Eltibule", Loc = "x:5 y:5 z:5" },
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        var portal = vm.LandmarkGroups.Single(g => g.Type == "Portal").Rows.Single()
            .Should().BeOfType<AreaLandmarkPortalRow>().Subject;
        portal.DescDisplay.Should().Be(" — Return to Eltibule");
        portal.LocDisplay.Should().Be(" · x:5 y:5 z:5");
    }

    [Fact]
    public void PortalRow_MissingDesc_DescDisplayEmpty()
    {
        // The Run binding stays attached but renders empty; visually the " — " separator
        // collapses with it. Regression net for the empty-string idiom (Run can't carry
        // Visibility, so the row's *Display getters paper over null fields).
        var stub = new StubReferenceData
        {
            LandmarksMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new PocoLandmark { Name = "Portal A", Type = "Portal", Loc = "x:0 y:0 z:0" },
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        var portal = vm.LandmarkGroups.Single(g => g.Type == "Portal").Rows.Single()
            .Should().BeOfType<AreaLandmarkPortalRow>().Subject;
        portal.DescDisplay.Should().BeEmpty();
        portal.LocDisplay.Should().Be(" · x:0 y:0 z:0");
    }

    [Fact]
    public void LandmarkRows_SortedAlphabeticallyWithinGroup()
    {
        var stub = new StubReferenceData
        {
            LandmarksMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new PocoLandmark { Name = "C Portal", Type = "Portal", Loc = "x:0 y:0 z:0" },
                    new PocoLandmark { Name = "A Portal", Type = "Portal", Loc = "x:1 y:1 z:1" },
                    new PocoLandmark { Name = "B Portal", Type = "Portal", Loc = "x:2 y:2 z:2" },
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        vm.LandmarkGroups.Single(g => g.Type == "Portal").Rows.Select(r => r.Name)
            .Should().Equal("A Portal", "B Portal", "C Portal");
    }

    [Fact]
    public void GroupHeading_IncludesPerTypeCount()
    {
        var stub = new StubReferenceData
        {
            LandmarksMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new PocoLandmark { Name = "P1", Type = "Portal", Loc = "x:0 y:0 z:0" },
                    new PocoLandmark { Name = "P2", Type = "Portal", Loc = "x:1 y:1 z:1" },
                    new PocoLandmark { Name = "P3", Type = "Portal", Loc = "x:2 y:2 z:2" },
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        vm.LandmarkGroups.Single(g => g.Type == "Portal").Heading.Should().Be("Portals (3)");
    }

    private static (AreaDetailViewModel Vm, StubReferenceData RefData) Build(
        AreaEntry area,
        StubReferenceData? refData = null,
        SilmarillionSettings? settings = null)
    {
        refData ??= new StubReferenceData();
        settings ??= new SilmarillionSettings();
        var navigator = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var nameResolver = new ReferenceDataEntityNameResolver(refData);
        var openCommand = new RelayCommand<EntityRef?>(_ => { });
        var vm = new AreaDetailViewModel(area, refData, navigator, nameResolver, settings, openCommand);
        return (vm, refData);
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, IReadOnlyList<PocoLandmark>> LandmarksMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<NpcEntry>> NpcsByAreaMap { get; } = new(StringComparer.Ordinal);

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
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
