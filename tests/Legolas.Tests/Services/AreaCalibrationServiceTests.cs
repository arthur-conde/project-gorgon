using System.IO;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Mithril.MapCalibration;
using Mithril.MapCalibration.DependencyInjection;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Misc;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Npc = Mithril.Reference.Models.Npcs.Npc;
using Quest = Mithril.Reference.Models.Quests.Quest;

namespace Legolas.Tests.Services;

public class AreaCalibrationServiceTests
{
    private static (AreaCalibrationService svc, FakeProjector proj, LegolasSettings settings, IMapCalibrationService mapCal)
        Build(FakeRefData refData)
    {
        var settings = new LegolasSettings();
        var proj = new FakeProjector();
        var saver = new SettingsAutoSaver<LegolasSettings>(new InMemoryStore(settings), settings);
        var mapCalDir = Path.Combine(Path.GetTempPath(), "mithril-mapcal-tests", Guid.NewGuid().ToString("N"));
        var mapCal = MapCalibrationServiceCollectionExtensions.Build(mapCalDir);
        var svc = new AreaCalibrationService(refData, settings, proj, saver, mapCal);
        return (svc, proj, settings, mapCal);
    }

    /// <summary>
    /// Seed a persisted calibration through both stores, mirroring the dual-write
    /// invariant <see cref="AreaCalibrationService.CalibrateCurrentArea"/> upholds
    /// during the #836 transition window. Tests use this rather than reaching into
    /// <see cref="LegolasSettings.AreaCalibrations"/> directly so reads (which now
    /// go through <see cref="IMapCalibrationService"/>) see the seeded value.
    /// </summary>
    private static void Seed(IMapCalibrationService mapCal, LegolasSettings settings, string areaKey, AreaCalibration cal)
    {
        mapCal.SaveUserRefinement(areaKey, cal);
        settings.AreaCalibrations[areaKey] = cal;
    }

    [Fact]
    public void Unknown_area_key_records_key_as_friendly_name_with_no_refs()
    {
        // SelectArea with a key that isn't in the gazetteer falls back to using
        // the key as the friendly name verbatim — the area-picker bypass path
        // (PlayerAreaTracker keys are always in-game-real, but a test/dev key
        // shouldn't crash the consumer).
        var (svc, _, _, _) = Build(new FakeRefData());

        svc.SelectArea("AreaNowheresville");

        svc.CurrentAreaKey.Should().Be("AreaNowheresville");
        svc.CurrentAreaFriendlyName.Should().Be("AreaNowheresville");
        svc.CurrentAreaReferences.Should().BeEmpty();
        svc.IsCurrentAreaCalibrated.Should().BeFalse();
    }

    [Fact]
    public void Entering_a_calibrated_area_applies_the_persisted_calibration_to_the_projector()
    {
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var (svc, proj, settings, mapCal) = Build(refData);
        var persisted = new AreaCalibration(3.0, 0.5, 11, 22, 4, 0.9);
        Seed(mapCal, settings, "AreaEltibule", persisted);

        // PlayerLogIngestionService.ApplyAreaIfChanged drives SelectArea with
        // the internal area key from PlayerAreaTracker — exercise that path.
        svc.SelectArea("AreaEltibule");

        svc.IsCurrentAreaCalibrated.Should().BeTrue();
        svc.CurrentCalibration.Should().Be(persisted);
        proj.LastApplied.Should().Be(persisted);
    }

    [Fact]
    public void Entering_an_uncalibrated_area_builds_references_and_does_not_touch_projector()
    {
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
            NpcsByKey =
            {
                ["NPC_Marn"] = new Npc { Name = "Marn", AreaName = "AreaEltibule", Pos = "x:10 y:0 z:20" },
                ["NPC_NoPos"] = new Npc { Name = "Ghost", AreaName = "AreaEltibule", Pos = null },
                ["NPC_Other"] = new Npc { Name = "Far", AreaName = "AreaSerbule", Pos = "x:1 y:0 z:1" },
            },
            LandmarksByArea =
            {
                ["AreaEltibule"] = new List<Landmark>
                {
                    new() { Name = "Teleport Circle", Type = "TeleportationPlatform", Loc = "x:5 y:1 z:6" },
                    new() { Name = "Broken", Type = "Portal", Loc = "not-a-loc" },
                },
            },
        };
        var (svc, proj, _, _) = Build(refData);

        svc.SelectArea("AreaEltibule");

        proj.LastApplied.Should().BeNull(); // no persisted calibration → projector untouched
        svc.CurrentAreaReferences.Select(r => r.Name)
            .Should().BeEquivalentTo(new[] { "Marn", "Teleport Circle" });
        svc.CurrentAreaReferences.Should().ContainSingle(r => r.Kind == "NPC");
        svc.CurrentAreaReferences.Should().ContainSingle(r => r.Kind == "TeleportationPlatform");
    }

    [Fact]
    public void CalibrateCurrentArea_solves_persists_applies_and_raises_changed()
    {
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var (svc, proj, settings, mapCal) = Build(refData);
        svc.SelectArea("AreaEltibule");

        var changed = 0;
        svc.Changed += (_, _) => changed++;

        // Identity-ish transform: pixel == world ground plane (scale 1, rot 0).
        var placements = new (WorldCoord, PixelPoint)[]
        {
            (new WorldCoord(0, 0, 0), new PixelPoint(0, 0)),
            (new WorldCoord(100, 0, 0), new PixelPoint(100, 0)),
            (new WorldCoord(0, 0, 100), new PixelPoint(0, -100)), // north → up
        };

        var cal = svc.CalibrateCurrentArea(placements, calibrationZoom: 0.39);

        cal.Should().NotBeNull();
        cal!.Scale.Should().BeApproximately(1.0, 1e-6);
        cal.CalibrationZoom.Should().BeApproximately(0.39, 1e-9); // stamped + persisted
        settings.AreaCalibrations.Should().ContainKey("AreaEltibule");
        settings.AreaCalibrations["AreaEltibule"].Should().Be(cal);
        proj.LastApplied.Should().Be(cal);
        changed.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void CalibrateCurrentArea_returns_null_with_fewer_than_two_placements_or_no_area()
    {
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var (svc, _, settings, mapCal) = Build(refData);

        // No current area yet.
        svc.CalibrateCurrentArea(new (WorldCoord, PixelPoint)[]
        {
            (new WorldCoord(0, 0, 0), new PixelPoint(0, 0)),
            (new WorldCoord(1, 0, 1), new PixelPoint(1, 1)),
        }).Should().BeNull();

        svc.SelectArea("AreaEltibule");
        svc.CalibrateCurrentArea(new (WorldCoord, PixelPoint)[]
        {
            (new WorldCoord(0, 0, 0), new PixelPoint(0, 0)),
        }).Should().BeNull();

        settings.AreaCalibrations.Should().NotContainKey("AreaEltibule");
    }

    [Fact]
    public void SelectArea_sets_current_area_builds_refs_and_applies_persisted()
    {
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
            NpcsByKey = { ["NPC_Marn"] = new Npc { Name = "Marn", AreaName = "AreaEltibule", Pos = "x:1 y:0 z:2" } },
        };
        var (svc, proj, settings, mapCal) = Build(refData);
        var persisted = new AreaCalibration(2, 0.1, 5, 6, 3, 0.5);
        Seed(mapCal, settings, "AreaEltibule", persisted);

        svc.SelectArea("AreaEltibule");

        svc.CurrentAreaKey.Should().Be("AreaEltibule");
        svc.CurrentAreaFriendlyName.Should().Be("Eltibule");
        svc.CurrentAreaReferences.Should().ContainSingle(r => r.Name == "Marn");
        proj.LastApplied.Should().Be(persisted);
    }

    [Fact]
    public void AllAreas_lists_every_area_sorted_by_friendly_name()
    {
        var refData = new FakeRefData
        {
            AreasByKey =
            {
                ["AreaServbule"] = new AreaEntry("AreaServbule", "Serbule", ""),
                ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", ""),
                ["AreaAnagoge"] = new AreaEntry("AreaAnagoge", "Anagoge Island", ""),
            },
        };
        var (svc, _, _, _) = Build(refData);

        svc.AllAreas.Select(a => a.FriendlyName)
            .Should().ContainInOrder("Anagoge Island", "Eltibule", "Serbule");
    }

    [Fact]
    public void NoteSurvey_reraises_as_SurveyObserved()
    {
        var (svc, _, _, _) = Build(new FakeRefData());
        CalibrationSurveyObservation? seen = null;
        svc.SurveyObserved += (_, o) => seen = o;

        svc.NoteSurvey("Iron Vein", new MetreOffset(12, -7));

        seen.Should().NotBeNull();
        seen!.Name.Should().Be("Iron Vein");
        seen.Offset.Should().Be(new MetreOffset(12, -7));
    }

    [Fact]
    public void ClearCurrentAreaCalibration_removes_and_raises_changed()
    {
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var (svc, _, settings, mapCal) = Build(refData);
        Seed(mapCal, settings, "AreaEltibule", new AreaCalibration(1, 0, 0, 0, 2, 0));
        svc.SelectArea("AreaEltibule");
        svc.IsCurrentAreaCalibrated.Should().BeTrue();

        var changed = 0;
        svc.Changed += (_, _) => changed++;

        svc.ClearCurrentAreaCalibration();

        settings.AreaCalibrations.Should().NotContainKey("AreaEltibule");
        svc.IsCurrentAreaCalibrated.Should().BeFalse();
        changed.Should().Be(1);
    }

    [Fact]
    public void Dual_write_does_not_touch_legacy_field_when_shared_service_throws_on_Save()
    {
        // Round-4 review #2: pin the post-round-3 ordering invariant.
        // CalibrateCurrentArea must call IMapCalibrationService.SaveUserRefinement
        // BEFORE writing _settings.AreaCalibrations[key]. A future contributor
        // cleaning up "awkward ordering" who re-swaps the order will trip this
        // test, because the throwing fake fails before the legacy write would
        // run.
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var settings = new LegolasSettings();
        var proj = new FakeProjector();
        var saver = new SettingsAutoSaver<LegolasSettings>(new InMemoryStore(settings), settings);
        var throwingMapCal = new ThrowingMapCalibrationService();
        var svc = new AreaCalibrationService(refData, settings, proj, saver, throwingMapCal);
        svc.SelectArea("AreaEltibule");

        var placements = new[]
        {
            (new WorldCoord(0, 0, 0), new PixelPoint(0, 0)),
            (new WorldCoord(100, 0, 0), new PixelPoint(100, 0)),
            (new WorldCoord(0, 0, 100), new PixelPoint(0, -100)),
        };

        FluentActions.Invoking(() => svc.CalibrateCurrentArea(placements))
            .Should().Throw<System.IO.IOException>();

        settings.AreaCalibrations.Should().NotContainKey("AreaEltibule",
            "shared-service throw must leave the legacy field untouched so retry starts from a clean state");
    }

    [Fact]
    public void Dual_clear_does_not_touch_legacy_field_when_shared_service_throws_on_Clear()
    {
        // Round-4 review #2: same invariant for ClearCurrentAreaCalibration.
        // Pre-round-3 order (legacy first) could leave a permanent orphan in
        // refinements.json that no migration could recover.
        var refData = new FakeRefData
        {
            AreasByKey = { ["AreaEltibule"] = new AreaEntry("AreaEltibule", "Eltibule", "") },
        };
        var settings = new LegolasSettings();
        var prior = new AreaCalibration(2, 0, 5, 6, 3, 0.5);
        settings.AreaCalibrations["AreaEltibule"] = prior;
        var proj = new FakeProjector();
        var saver = new SettingsAutoSaver<LegolasSettings>(new InMemoryStore(settings), settings);
        var throwingMapCal = new ThrowingMapCalibrationService();
        var svc = new AreaCalibrationService(refData, settings, proj, saver, throwingMapCal);
        svc.SelectArea("AreaEltibule");

        FluentActions.Invoking(() => svc.ClearCurrentAreaCalibration())
            .Should().Throw<System.IO.IOException>();

        settings.AreaCalibrations.Should().ContainKey("AreaEltibule",
            "shared-service throw must leave the legacy field untouched so retry starts from a consistent state");
    }

    // ---- fakes ------------------------------------------------------------

    /// <summary>
    /// IMapCalibrationService that throws IOException on every write — used to
    /// verify AreaCalibrationService's dual-write ordering. Reads return null
    /// (uncalibrated) which is fine for the ordering tests.
    /// </summary>
    private sealed class ThrowingMapCalibrationService : IMapCalibrationService
    {
        public event EventHandler<string>? Changed { add { } remove { } }
        public bool IsCalibrated(string areaKey) => false;
        public AreaCalibration? GetCalibration(string areaKey) => null;
        public PixelPoint? WorldToWindow(string areaKey, WorldCoord world, double currentZoom) => null;
        public WorldCoord? WindowToWorld(string areaKey, PixelPoint pixel, double currentZoom) => null;
        public IReadOnlyDictionary<string, AreaCalibration> AllCalibrations { get; } =
            new Dictionary<string, AreaCalibration>(StringComparer.Ordinal);
        public IReadOnlyList<AreaCalibration> GetAllSources(string areaKey) => Array.Empty<AreaCalibration>();
        public void SaveUserRefinement(string areaKey, AreaCalibration calibration) =>
            throw new System.IO.IOException("simulated disk failure");
        public void ClearUserRefinement(string areaKey) =>
            throw new System.IO.IOException("simulated disk failure");
        public int ImportUserRefinements(IReadOnlyDictionary<string, AreaCalibration> source) =>
            throw new System.IO.IOException("simulated disk failure");
    }

    private sealed class FakeProjector : ICoordinateProjector
    {
        public AreaCalibration? LastApplied { get; private set; }
        public double Scale => 1;
        public double RotationRadians => 0;
        public PixelPoint Origin => PixelPoint.Zero;
        public PixelPoint Project(MetreOffset offset) => PixelPoint.Zero;
        public void SetOrigin(PixelPoint origin) { }
        public void CalibrateFromClick(PixelPoint playerPixel, PixelPoint click, MetreOffset offset) { }
        public void Refit(IReadOnlyList<(MetreOffset Offset, PixelPoint Pixel)> corrections) { }
        public void ApplyCalibration(AreaCalibration calibration) => LastApplied = calibration;
    }

    private sealed class InMemoryStore : ISettingsStore<LegolasSettings>
    {
        private LegolasSettings _v;
        public InMemoryStore(LegolasSettings v) => _v = v;
        public string FilePath => "(memory)";
        public LegolasSettings Load() => _v;
        public Task<LegolasSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(_v);
        public Task SaveAsync(LegolasSettings value, CancellationToken ct = default) { _v = value; return Task.CompletedTask; }
        public void Save(LegolasSettings value) => _v = value;
    }

    private sealed class FakeRefData : IReferenceDataService
    {
        public Dictionary<string, AreaEntry> AreasByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Npc> NpcsByKey { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<Landmark>> LandmarksByArea { get; } = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, AreaEntry> Areas => AreasByKey;
        public IReadOnlyDictionary<string, Npc> NpcsByInternalName => NpcsByKey;
        public IReadOnlyDictionary<string, IReadOnlyList<Landmark>> Landmarks =>
            LandmarksByArea.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Landmark>)kv.Value, StringComparer.Ordinal);

        // Required (non-default) members — empty, mirrors the established Legolas
        // test stub pattern (LegolasReportServiceTests.StubRefData).
        public IReadOnlyList<string> Keys { get; } = [];
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
        public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public event EventHandler<string>? FileUpdated;
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        private void Suppress() => FileUpdated?.Invoke(this, "");
    }
}
