using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf;
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
    }

    [Fact]
    public void NpcChips_CappedAtUsedInChipCap_PopupReportsTotalCount()
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

        vm.NpcChips.Should().HaveCount(12, "the in-pane cluster is capped");
        vm.NpcsTotal.Should().Be(30);
        vm.NpcsPopup.Should().NotBeNull();
        // The defining #318 assertion: "View all N" == distinct index members, NOT the cap.
        vm.NpcsPopup!.TotalCount.Should().Be(30,
            "the popup is fed the index directly — the cap never affects its count.");
        vm.ShowNpcsPopupCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void NpcsPopup_IsFlat_SingleReasonInArea()
    {
        var stub = new StubReferenceData
        {
            NpcsByAreaMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new NpcEntry("NPC_Joeh", "Joeh", "Serbule", [], [], []),
                    new NpcEntry("NPC_Marna", "Marna", "Serbule", [], [], []),
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        var popup = vm.NpcsPopup!;
        popup.IsFlat.Should().BeTrue("single reason (InArea) ⇒ flat list, #318 Discipline");
        popup.Sections.Should().ContainSingle().Which.Label.Should().Be("NPCs in this area");
        popup.FlatChips.Select(c => c.Reference.InternalName)
            .Should().BeEquivalentTo(new[] { "NPC_Joeh", "NPC_Marna" });
    }

    [Fact]
    public void NpcsPopup_NullWhenNoNpcsInArea()
    {
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"));
        vm.NpcChips.Should().BeEmpty();
        vm.NpcsPopup.Should().BeNull("no NPCs means no popup — the affordance hides.");
        vm.NpcsTotal.Should().Be(0);
        vm.HasNpcs.Should().BeFalse();
        vm.ShowNpcsPopupCommand.CanExecute(null).Should().BeFalse();
    }

    // ── #318 Gate C (merge-blocking) — NPCs surface ────────────────────────────

    [Fact]
    public void GateC_NpcsPopup_MembershipEqualsIndex_MemberOnlyInIndexStillAppears()
    {
        // The load-bearing regression that would have caught the original dual-derivation
        // bug for THIS surface. The pre-#318 "NPCs in this area" deep-linked via the
        // NpcByArea synthetic kind, whose kind target re-derived the set as the query
        // string `AreaName = "<areaKey>"`. If the index ever carried a member the query
        // string did NOT (an NPC present in NpcsByAreaWithReason but not re-derivable
        // from a naive AreaName filter), the chip opened a list missing that member. The
        // popup-from-index has no query between the set and the screen, so a member
        // present ONLY in the index must still appear — with its InArea provenance — and
        // the count must equal the distinct index membership exactly.
        var direct = new NpcEntry("NPC_Joeh", "Joeh", "Serbule", [], [], []);
        // An NPC whose membership comes from a match record only — the "non-primary path"
        // analogue: it lives in the provenance index, never re-derivable from a naive
        // AreaName query. It must still surface in the popup.
        var indexOnly = new NpcEntry("NPC_IndexOnly", "Index Only", "Serbule", [], [], []);
        var stub = new StubReferenceData
        {
            // Seed the provenance index directly with BOTH members so the test asserts
            // popup membership == index membership, independent of any query.
            NpcsByAreaWithReasonOverride =
            {
                ["AreaSerbule"] = new[]
                {
                    new NpcByAreaMatch(direct, NpcByAreaMatchReason.InArea),
                    new NpcByAreaMatch(indexOnly, NpcByAreaMatchReason.InArea),
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        var popup = vm.NpcsPopup;
        popup.Should().NotBeNull();
        popup!.TotalCount.Should().Be(2, "View all N == distinct index members");
        popup.IsFlat.Should().BeTrue("single reason (InArea) ⇒ flat list");
        popup.FlatChips.Select(c => c.Reference.InternalName).Should().BeEquivalentTo(
            new[] { "NPC_Joeh", "NPC_IndexOnly" },
            "every index member appears — there is no query that could drop one.");
        popup.Sections.Should().ContainSingle().Which.Label.Should().Be("NPCs in this area");
        // The capped cluster is a view over the SAME ordered list — membership identical.
        vm.NpcChips.Select(c => c.Reference.InternalName).Should().BeEquivalentTo(
            new[] { "NPC_Joeh", "NPC_IndexOnly" });
        vm.NpcsTotal.Should().Be(2);
    }

    [Fact]
    public void NpcsPopup_openingPopup_doesNotTouchNavigator_noHistoryPushed()
    {
        // #318 — opening the popup must NOT push navigator back/forward history (the #229
        // non-navigating contract, mirroring TryOpenInWindow and the surface-1
        // ItemDetailViewModel test). The opener is swapped for a capturing no-op so no
        // window spawns; assert navigator state is pristine before and after.
        var stub = new StubReferenceData
        {
            NpcsByAreaMap =
            {
                ["AreaSerbule"] = new[] { new NpcEntry("NPC_Joeh", "Joeh", "Serbule", [], [], []) },
            },
        };
        var nav = new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>());
        var vm = BuildWith(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub, nav);

        var prior = AreaDetailViewModel.ProvenancePopupOpener;
        ProvenancePopupViewModel? captured = null;
        AreaDetailViewModel.ProvenancePopupOpener = (popupVm, _) => captured = popupVm;
        try
        {
            nav.Current.Should().BeNull();
            nav.CanGoBack.Should().BeFalse();
            nav.CanGoForward.Should().BeFalse();

            vm.ShowNpcsPopupCommand.Execute(null);

            captured.Should().NotBeNull("the command invoked the opener with the built popup VM");
            // The defining assertion: opening the popup pushed no navigator state.
            nav.Current.Should().BeNull();
            nav.CanGoBack.Should().BeFalse();
            nav.CanGoForward.Should().BeFalse();
        }
        finally
        {
            AreaDetailViewModel.ProvenancePopupOpener = prior;
        }
    }

    // ── Landmarks (#311 fold-in) ───────────────────────────────────────────────

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

    [Fact]
    public void LandmarksPopup_IsSectionedByType_NotFlat()
    {
        // Landmark Type IS genuine provenance ("which kind of landmark"), so the popup is
        // a multi-section (provenance-sectioned) popup, NOT collapsed-to-flat. This is the
        // deliberate distinction from the NPCs surface (single trivial reason ⇒ flat).
        var stub = new StubReferenceData
        {
            LandmarksMap =
            {
                ["AreaSerbule"] = new[]
                {
                    new PocoLandmark { Name = "Pillar A", Type = "MeditationPillar", Combo = "4017", Loc = "x:1 y:1 z:1" },
                    new PocoLandmark { Name = "Portal A", Type = "Portal", Desc = "Return", Loc = "x:0 y:0 z:0" },
                    new PocoLandmark { Name = "Portal B", Type = "Portal", Desc = "Exit", Loc = "x:2 y:2 z:2" },
                },
            },
        };
        var (vm, _) = Build(new AreaEntry("AreaSerbule", "Serbule", "Serbule"), stub);

        var popup = vm.LandmarksPopup!;
        popup.Should().NotBeNull();
        popup.IsFlat.Should().BeFalse("landmark Type is genuine provenance — sectioned, never flat");
        popup.Sections.Select(s => s.Label).Should().Equal(
            new[] { "Meditation Pillars", "Portals" },
            "sections follow the gameplay-relevance order: pillars → portals → platforms");
        popup.Sections.Single(s => s.Label == "Portals").Chips.Should().HaveCount(2);
        // Folded label: name · detail (Combo for pillars, Desc · Loc for portals).
        popup.Sections.Single(s => s.Label == "Meditation Pillars").Chips.Single()
            .DisplayName.Should().Be("Pillar A · Combo: 4017 · x:1 y:1 z:1");
        popup.Sections.SelectMany(s => s.Chips).Should().OnlyContain(c => !c.IsNavigable,
            "landmarks aren't navigable entities — chips are inert");
    }

    [Fact]
    public void GateC_LandmarksPopup_HighCardinality_TotalEqualsDistinctMembers_Virtualized()
    {
        // #318 Gate C + the #311 high-cardinality virtualization sanity assertion. The
        // ~547-row Massive Tourmaline precedent (#259) must not regress to a
        // non-virtualized list. There is no separate non-virtualized list path: the FULL
        // landmark set is materialized once and rendered ONLY through the shared
        // ProvenancePopupWindow's recycling VirtualizingStackPanel. This test seeds a
        // 547-landmark area and asserts (a) the popup carries the full distinct set
        // (TotalCount == 547, independent of the in-pane preview), (b) every landmark
        // appears with a DISTINCT reference (so TotalCount's Distinct() can't collapse
        // them — the virtualized list renders all 547 rows), (c) section provenance is
        // intact. The shared control's ProvenanceChipListStyle (asserted in
        // ProvenancePopupWindow.xaml) is the recycling VirtualizingStackPanel; routing
        // through it is the structural guarantee that this path is virtualized.
        const int n = 547;
        var landmarks = Enumerable.Range(0, n)
            .Select(i => new PocoLandmark
            {
                Name = $"Portal {i:000}",
                Type = "Portal",
                Desc = $"Exit {i}",
                Loc = $"x:{i} y:{i} z:{i}",
            })
            .ToArray();
        var stub = new StubReferenceData
        {
            LandmarksMap = { ["AreaTourmaline"] = landmarks },
        };
        var (vm, _) = Build(new AreaEntry("AreaTourmaline", "Massive Tourmaline", "Tourmaline"), stub);

        vm.LandmarksTotal.Should().Be(n);
        var popup = vm.LandmarksPopup!;
        popup.Should().NotBeNull();
        popup.TotalCount.Should().Be(n,
            "the popup carries the full distinct set fed from the index, not a preview cap");
        popup.Sections.Should().ContainSingle("only Portals in this fixture")
            .Which.Chips.Should().HaveCount(n);
        // Each landmark must have a DISTINCT reference, otherwise ProvenancePopupViewModel's
        // Distinct()-by-Reference TotalCount would collapse them and the (virtualized) list
        // would silently render fewer than 547 rows.
        popup.Sections.Single().Chips.Select(c => c.Reference).Distinct().Should().HaveCount(n);
        popup.Sections.Single().Chips.Should().OnlyContain(c => !c.IsNavigable);
    }

    // ── Real-data sanity ──────────────────────────────────────────────────────

    [Fact]
    public void RealBundledArea_Serbule_ProjectsSensibly()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(bundled, "landmarks.json"))) return;

        var refData = BuildRealRefData(bundled);
        if (refData is null) return;

        var areasVm = new AreasTabViewModel(refData,
            new SilmarillionReferenceNavigator(new[] { (IReferenceKindTarget)new StubKindTarget(EntityKind.Npc), new StubKindTarget(EntityKind.Area) }),
            new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings());

        var serbule = areasVm.AllAreas.FirstOrDefault(a => a.Key == "AreaSerbule");
        serbule.Should().NotBeNull("AreaSerbule is a stable area in bundled areas.json");
        areasVm.SelectedArea = serbule;

        var detail = areasVm.DetailViewModel!;
        detail.DisplayName.Should().NotBeNullOrEmpty();
        detail.DisplayName.Should().NotBe("(unknown)");
        detail.HasNpcs.Should().BeTrue("AreaSerbule has multiple NPCs in bundled npcs.json");
        detail.NpcChips.Should().OnlyContain(c => !string.IsNullOrEmpty(c.DisplayName));
        detail.NpcChips.Should().OnlyContain(c => !c.DisplayName.StartsWith("NPC_"),
            because: "the resolver should project envelope keys to friendly names");
        // The popup is fed the index directly: count == distinct index membership.
        detail.NpcsPopup.Should().NotBeNull();
        detail.NpcsPopup!.TotalCount.Should().Be(detail.NpcsTotal);
        detail.NpcsPopup.IsFlat.Should().BeTrue("single-reason InArea ⇒ flat");

        detail.LandmarkGroups.Should().OnlyContain(g => g.Rows.Count > 0,
            because: "empty groups are filtered out by BuildLandmarks");
        detail.LandmarkGroups.SelectMany(g => g.Rows).Should().OnlyContain(r => !string.IsNullOrEmpty(r.Name));
        if (detail.HasLandmarks)
        {
            detail.LandmarksPopup.Should().NotBeNull();
            detail.LandmarksPopup!.TotalCount.Should().Be(detail.LandmarksTotal);
            detail.LandmarksPopup.IsFlat.Should().BeFalse("landmarks are sectioned by Type");
        }
    }

    [Fact]
    public void RealBundledArea_LargestLandmarkCluster_PartitionsWithoutUnknownTypes()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        if (!File.Exists(Path.Combine(bundled, "landmarks.json"))) return;

        var refData = BuildRealRefData(bundled);
        if (refData is null) return;

        var biggestArea = refData.Landmarks
            .OrderByDescending(kv => kv.Value.Count)
            .First();
        if (!refData.Areas.TryGetValue(biggestArea.Key, out var areaEntry)) return;

        var areasVm = new AreasTabViewModel(refData,
            new SilmarillionReferenceNavigator(Array.Empty<IReferenceKindTarget>()),
            new ReferenceDataEntityNameResolver(refData),
            new SilmarillionSettings());
        areasVm.SelectedArea = areasVm.AllAreas.First(a => a.Key == areaEntry.Key);

        var detail = areasVm.DetailViewModel!;
        var groups = detail.LandmarkGroups;
        groups.Should().NotBeEmpty();
        groups.Select(g => g.Type).Should().BeSubsetOf(new[] { "MeditationPillar", "Portal", "TeleportationPlatform" },
            because: "the corpus only carries those three Types; a fallback '(unknown)' indicates either a future PG patch shipping a new Type or a POCO binding break");
        // Popup membership == full materialized landmark count for the area.
        detail.LandmarksPopup!.TotalCount.Should().Be(biggestArea.Value.Count,
            "the popup is the only path to the full set and must carry every landmark");
    }

    private static IReferenceDataService? BuildRealRefData(string bundled)
    {
        try
        {
            var cacheDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(cacheDir);
            using var http = new System.Net.Http.HttpClient(new ThrowingHttpHandler());
            return new ReferenceDataService(cacheDir, http, bundledDir: bundled);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ThrowingHttpHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("HTTP must not be called in this test");
    }

    private sealed class StubKindTarget : IReferenceKindTarget
    {
        public StubKindTarget(EntityKind kind) => Kind = kind;
        public EntityKind Kind { get; }
        public int TabIndex => 0;
        public bool TrySelectByInternalName(string internalName) => true;
        public bool TryOpenInWindow() => false;
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

    private static AreaDetailViewModel BuildWith(
        AreaEntry area,
        StubReferenceData refData,
        SilmarillionReferenceNavigator navigator)
    {
        var nameResolver = new ReferenceDataEntityNameResolver(refData);
        var openCommand = new RelayCommand<EntityRef?>(_ => { });
        return new AreaDetailViewModel(area, refData, navigator, nameResolver, new SilmarillionSettings(), openCommand);
    }

    private sealed class StubReferenceData : IReferenceDataService
    {
        public Dictionary<string, IReadOnlyList<PocoLandmark>> LandmarksMap { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<NpcEntry>> NpcsByAreaMap { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Explicit override for the provenance index (#318 Gate C — seed it independently
        /// of <see cref="NpcsByAreaMap"/> to prove popup membership == index membership
        /// with NO query in between). When unset, derive from the same NpcsByAreaMap
        /// fixture so existing setups feed the popup-from-index without restating data.
        /// </summary>
        public Dictionary<string, IReadOnlyList<NpcByAreaMatch>> NpcsByAreaWithReasonOverride { get; } = new(StringComparer.Ordinal);

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

        public IReadOnlyDictionary<string, IReadOnlyList<NpcByAreaMatch>> NpcsByAreaWithReason
        {
            get
            {
                if (NpcsByAreaWithReasonOverride.Count > 0)
                    return NpcsByAreaWithReasonOverride;
                // Derive from the same NpcsByAreaMap accumulation — single materialization,
                // mirroring ReferenceDataService.BuildAreaNpcCrossLinkIndex so tests that
                // only seed NpcsByAreaMap still exercise the real popup-from-index path.
                return NpcsByAreaMap.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<NpcByAreaMatch>)kv.Value
                        .Select(n => new NpcByAreaMatch(n, NpcByAreaMatchReason.InArea))
                        .ToList(),
                    StringComparer.Ordinal);
            }
        }

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
