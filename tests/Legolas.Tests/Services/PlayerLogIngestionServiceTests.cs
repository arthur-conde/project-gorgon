using System.IO;
using System.Text;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests.TestSupport;
using Legolas.ViewModels;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Xunit;

namespace Legolas.Tests.Services;

/// <summary>
/// Post-#550 PR 3 (L1 migration): the service now subscribes through
/// <see cref="ILogStreamDriver"/> with
/// <see cref="ReplayMode.LiveOnly"/> + an optional persisted
/// <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/>. The pre-L1
/// <c>_liveSince</c> timestamp guard is gone; tests no longer rely on it
/// — they push live envelopes through the test L1 driver and assert the
/// handler's UI-bound state mutations.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class PlayerLogIngestionServiceTests : IDisposable
{
    private readonly string _tempDir =
        Mithril.TestSupport.TestPaths.CreateTempDir("legolas_playerlog");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // Identity calibration: ProjectWorld(x,y,z) = (x, z) → trivial assertions.
    private static AreaCalibration Identity() =>
        new(1.0, 0.0, 0.0, 0.0, 2, 0.0);

    private const string MapFx =
        "[08:25:39] LocalPlayer: ProcessMapFx((1236.00, 38.17, 2528.00), 25, 1, " +
        "\"Good Metal Slab is here\", ImportantInfo, \"The Good Metal Slab is 67m west and 1181m south.\")";

    private sealed record Fixture(
        PlayerLogIngestionService Service,
        TestLogStreamDriver Driver,
        SpyAreaCalibration Spy,
        SessionState Session,
        SurveyFlowController Flow,
        PlayerAreaTracker Tracker);

    private static Fixture Build(
        AreaCalibration? calibration = null, GameConfig? config = null)
    {
        var driver = new TestLogStreamDriver();
        // #514: the shared tracker owns its one-shot seed. Route the test's
        // config to the tracker so a startup-seed test drives the owned
        // self-seed (lazy-on-first-read of CurrentArea — which
        // ApplyAreaIfChanged does at gate-open).
        var tracker = new PlayerAreaTracker(new AreaTransitionParser(), config: config);
        var spy = new SpyAreaCalibration(calibration);
        var session = new SessionState();
        var settings = new LegolasSettings();
        var flow = new SurveyFlowController(session, settings);
        var gates = new ModuleGates();
        gates.For("legolas").Open();
        var motherlode = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(session),
            new FakePlayerPositionTracker(), new FakePlayerPinTracker());
        var svc = new PlayerLogIngestionService(
            driver, new PlayerLogParser(), tracker, spy, flow, session, motherlode,
            settings, gates,
            activeChar: null, ingestionStore: null);
        return new Fixture(svc, driver, spy, session, flow, tracker);
    }

    /// <summary>
    /// Build a live <see cref="LocalPlayerLogLine"/> from a full PG log
    /// line, stripping the optional <c>[ts]</c> prefix and the
    /// <c>LocalPlayer:</c> envelope (what L0.5 does in production).
    /// </summary>
    private static LocalPlayerLogLine LiveLine(string fullLine, long sequence = 0) =>
        new(new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero),
            StripEnvelope(fullLine),
            sequence,
            0);

    /// <summary>
    /// Mimic L0.5 envelope stripping: peel optional <c>[HH:MM:SS]</c>
    /// prefix and the mandatory <c>LocalPlayer:</c> actor token.
    /// </summary>
    private static string StripEnvelope(string line)
    {
        const int tsPrefixLen = 11;
        const string actor = "LocalPlayer: ";
        var idx = 0;
        if (line.Length > tsPrefixLen
            && line[0] == '['
            && line[3] == ':'
            && line[6] == ':'
            && line[9] == ']')
        {
            idx = tsPrefixLen;
        }
        if (idx + actor.Length <= line.Length
            && line.IndexOf(actor, idx, StringComparison.Ordinal) == idx)
        {
            idx += actor.Length;
        }
        return idx == 0 ? line : line.Substring(idx);
    }

    // ---- area→calibration bridge (Phase 2) -------------------------------

    [Fact]
    public async Task Area_load_line_applies_that_area_calibration_once()
    {
        var f = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine("[08:25:13] LOADING LEVEL AreaEltibule"));
            f.Driver.PushLive(LiveLine("[08:30:00] LOADING LEVEL AreaEltibule"));
            await f.Driver.DrainLocalPlayerAsync();
            f.Spy.SelectedAreas.Should().Equal("AreaEltibule");
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task Area_change_applies_each_distinct_area_in_order()
    {
        var f = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine("[08:25:13] LOADING LEVEL AreaEltibule"));
            f.Driver.PushLive(LiveLine("[08:57:37] LOADING LEVEL AreaSerbule"));
            await f.Driver.DrainLocalPlayerAsync();
            f.Spy.SelectedAreas.Should().Equal("AreaEltibule", "AreaSerbule");
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task ChooseCharacter_resets_latch_so_same_area_re_applies()
    {
        var f = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine("[08:25:13] LOADING LEVEL AreaEltibule"));
            f.Driver.PushLive(LiveLine("[08:57:00] LOADING LEVEL ChooseCharacter"));
            f.Driver.PushLive(LiveLine("[09:00:39] LOADING LEVEL AreaEltibule"));
            await f.Driver.DrainLocalPlayerAsync();
            f.Spy.SelectedAreas.Should().Equal("AreaEltibule", "AreaEltibule");
        }
        finally { await Stop(f, run, cts); }
    }

    /// <summary>
    /// #514 + #550 LiveOnly composition: PlayerAreaTracker self-seeds on
    /// first <see cref="PlayerAreaTracker.CurrentArea"/> read. The L1
    /// subscription is LiveOnly so we do NOT pump replay envelopes — the
    /// tracker's own log scan delivers the area before any live envelope.
    /// </summary>
    [Fact]
    public async Task Startup_seed_applies_current_area_before_live_lines()
    {
        var logPath = Path.Combine(_tempDir, "Player.log");
        File.WriteAllText(
            logPath,
            "LocalPlayer: ProcessAddItem(Apple(1), -1, True)\n" +
            "LOADING LEVEL AreaEltibule\n" +
            "LocalPlayer: ProcessAddPlayer(...)\n",
            new UTF8Encoding(false));

        var f = Build(config: new GameConfig { GameRoot = _tempDir });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => f.Spy.SelectedAreas.Count > 0, cts.Token);
            f.Spy.SelectedAreas.Should().Equal("AreaEltibule");
        }
        finally { await Stop(f, run, cts); }
    }

    // ---- absolute ProcessMapFx placement (Phase 3) -----------------------

    [Fact]
    public async Task Calibrated_area_places_one_absolute_pin_at_projected_pixel()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine(MapFx));
            await f.Driver.DrainLocalPlayerAsync();
            await WaitUntil(
                () => f.Session.LastLogEvent?.Contains("placed (absolute)") == true,
                cts.Token);

            f.Session.Surveys.Should().HaveCount(1);
            var pin = f.Session.Surveys[0];
            pin.Name.Should().Be("Good Metal Slab");
            pin.Model.World.Should().Be(new WorldCoord(1236.00, 38.17, 2528.00));
            pin.Model.PixelPos.Should().Be(new PixelPoint(1236.00, -2528.00));
            f.Session.SelectedSurvey.Should().BeSameAs(pin);
            f.Session.IsInventoryVisible.Should().BeTrue();
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task Duplicate_world_coord_within_radius_does_not_stack()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine(MapFx, sequence: 10));
            f.Driver.PushLive(LiveLine(
                MapFx.Replace("67m west and 1181m", "66m west and 1180m"),
                sequence: 11));
            await f.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => f.Session.Surveys.Count >= 1, cts.Token);

            f.Session.Surveys.Should().HaveCount(1);
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task Distinct_targets_place_separate_pins()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine(MapFx));
            f.Driver.PushLive(LiveLine(
                "[08:33:17] LocalPlayer: ProcessMapFx((1666.00, 36.95, 2620.00), 25, 1, " +
                "\"Good Metal Slab is here\", ImportantInfo, \"The Good Metal Slab is 604m west and 1073m south.\")"));
            await f.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => f.Session.Surveys.Count == 2, cts.Token);

            f.Session.Surveys.Should().HaveCount(2);
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task Uncalibrated_area_does_not_place_and_reports_diagnostic()
    {
        var f = Build(calibration: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine(MapFx));
            await f.Driver.DrainLocalPlayerAsync();
            await WaitUntil(
                () => f.Session.LastLogEvent?.Contains("not calibrated") == true,
                cts.Token);

            f.Session.Surveys.Should().BeEmpty();
            f.Session.LastLogEvent.Should().Contain("not calibrated");
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task Motherlode_mode_ignores_absolute_targets()
    {
        var f = Build(calibration: Identity());
        f.Session.Mode = SessionMode.Motherlode;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine(MapFx));
            await f.Driver.DrainLocalPlayerAsync();
            await WaitUntil(
                () => f.Session.LastLogEvent?.Contains("Motherlode") == true,
                cts.Token);

            f.Session.Surveys.Should().BeEmpty();
        }
        finally { await Stop(f, run, cts); }
    }

    // ---- replay drop semantics (post-L1 — LiveOnly + high-water) --------

    /// <summary>
    /// LiveOnly drops replay-phase envelopes structurally — the consumer
    /// never sees them, so no resurrection from session-start backlog.
    /// Replaces the pre-L1 <c>mt.Timestamp &lt; _liveSince</c> guard inside
    /// HandleMapTarget (which emitted a "log replay" diagnostic); the L1
    /// equivalent is silent at the consumer because the filter is upstream.
    /// </summary>
    [Fact]
    public async Task Replay_phase_envelopes_are_dropped_by_LiveOnly()
    {
        var f = Build(calibration: Identity());
        // Push a replay-phase envelope BEFORE the subscription starts so it
        // lands in the replay snapshot. The L1 LiveOnly mode drops it.
        f.Driver.PushReplay(LiveLine(MapFx));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            await f.Driver.DrainLocalPlayerAsync();
            f.Session.Surveys.Should().BeEmpty("LiveOnly drops replay-phase envelopes");
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task Live_target_after_startup_is_still_placed()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine(MapFx));
            await f.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => f.Session.Surveys.Count == 1, cts.Token);

            f.Session.Surveys.Should().HaveCount(1);
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task Non_accepting_flow_state_does_not_place()
    {
        var f = Build(calibration: Identity());
        f.Flow.RequestSetPosition(); // Listening → SettingPosition
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine(MapFx));
            await f.Driver.DrainLocalPlayerAsync();
            await WaitUntil(
                () => f.Session.LastLogEvent?.Contains("survey flow is") == true,
                cts.Token);

            f.Session.Surveys.Should().BeEmpty();
            f.Session.LastLogEvent.Should().Contain("SettingPosition");
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task Gathering_still_accepts_new_targets()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine(MapFx));
            await WaitUntil(() => f.Session.Surveys.Count == 1, cts.Token);
            f.Flow.OptimizeRoute();

            f.Driver.PushLive(LiveLine(
                "[08:33:17] LocalPlayer: ProcessMapFx((1666.00, 36.95, 2620.00), 25, 1, " +
                "\"Good Metal Slab is here\", ImportantInfo, \"The Good Metal Slab is 604m west and 1073m south.\")"));
            await f.Driver.DrainLocalPlayerAsync();
            await WaitUntil(() => f.Session.Surveys.Count == 2, cts.Token);

            f.Session.Surveys.Should().HaveCount(2);
        }
        finally { await Stop(f, run, cts); }
    }

    [Fact]
    public async Task ProcessMapPin_lines_are_ignored_by_this_service()
    {
        var f = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = f.Service.StartAsync(cts.Token);
        try
        {
            f.Driver.PushLive(LiveLine(
                "[08:32:20] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (1145.39, 0.00, 1323.40), \"Calib 1\")"));
            f.Driver.PushLive(LiveLine(
                "[08:32:21] LocalPlayer: ProcessMapPinRemove(1, 0, 0, (1145.39, 0.00, 1323.40), \"Calib 1\")"));
            await f.Driver.DrainLocalPlayerAsync();

            f.Session.Surveys.Should().BeEmpty();
        }
        finally { await Stop(f, run, cts); }
    }

    // ---- high-water restart simulation (#550 capability F) --------------

    /// <summary>
    /// Restart-resume simulation: session 1 processes envelopes with
    /// Sequence 1..N. A new <c>PlayerLogIngestionService</c> starts with
    /// <c>SkipProcessedHighWater = N</c> (the persisted value from session
    /// 1) and is fed envelopes 1..M (M &gt; N). Only N+1..M reach the handler.
    /// Replaces the pre-L1 in-memory <c>_liveSince</c> guard — high-water
    /// survives a Mithril restart whereas <c>_liveSince</c> reset to "now"
    /// on every cold start, suppressing legitimate session-start lines.
    /// </summary>
    [Fact]
    public async Task High_water_filter_drops_already_processed_envelopes_on_restart()
    {
        // Use a per-test ingestion store to capture the persisted high-water.
        var storeDir = Path.Combine(_tempDir, "ingestion");
        var store = new Mithril.Shared.Character.PerCharacterStore<LegolasIngestionState>(
            storeDir,
            "legolas-ingestion.json",
            LegolasIngestionStateJsonContext.Default.LegolasIngestionState);
        var activeChar = new FakeActiveCharacterService("Test", "Alpha");

        // Pre-load the store with high-water = 5 to simulate a prior session.
        store.Save("Test", "Alpha", new LegolasIngestionState
        {
            PlayerLogHighWaterSequence = 5,
        });

        var driver = new TestLogStreamDriver();
        var tracker = new PlayerAreaTracker(new AreaTransitionParser());
        var spy = new SpyAreaCalibration(Identity());
        var session = new SessionState();
        var settings = new LegolasSettings();
        var flow = new SurveyFlowController(session, settings);
        var gates = new ModuleGates();
        gates.For("legolas").Open();
        var motherlode = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(session),
            new FakePlayerPositionTracker(), new FakePlayerPinTracker());
        var svc = new PlayerLogIngestionService(
            driver, new PlayerLogParser(), tracker, spy, flow, session, motherlode,
            settings, gates,
            activeChar: activeChar, ingestionStore: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            // Sequence 3 — below high-water (5) → dropped.
            driver.PushLive(LiveLine(MapFx, sequence: 3));
            // Sequence 5 — equal to high-water → dropped (<=).
            driver.PushLive(LiveLine(
                "[08:33:17] LocalPlayer: ProcessMapFx((2000.0, 0.0, 2000.0), 25, 1, " +
                "\"Stale is here\", ImportantInfo, \"The Stale is 1m west and 1m north.\")",
                sequence: 5));
            // Sequence 6 — above high-water → DELIVERED.
            driver.PushLive(LiveLine(
                "[08:34:17] LocalPlayer: ProcessMapFx((3000.0, 0.0, 3000.0), 25, 1, " +
                "\"Fresh is here\", ImportantInfo, \"The Fresh is 1m west and 1m north.\")",
                sequence: 6));

            await driver.DrainLocalPlayerAsync();
            await WaitUntil(() => session.Surveys.Count == 1, cts.Token);

            session.Surveys.Should().HaveCount(1, "only Sequence 6 (> high-water 5) reaches the handler");
            session.Surveys[0].Name.Should().Be("Fresh");
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            _ = run;
            svc.Dispose();
            driver.Dispose();
        }
    }

    /// <summary>
    /// Sanity check on the other half: after a session that handles
    /// Sequence 10, the persisted high-water is at least 10. This is the
    /// "byte-equivalence" half of the restart test — together with the
    /// "high-water drops already-processed" test above, it proves the
    /// round trip.
    /// </summary>
    [Fact]
    public async Task Handler_advances_persisted_high_water()
    {
        var storeDir = Path.Combine(_tempDir, "ingestion-advance");
        var store = new Mithril.Shared.Character.PerCharacterStore<LegolasIngestionState>(
            storeDir,
            "legolas-ingestion.json",
            LegolasIngestionStateJsonContext.Default.LegolasIngestionState);
        var activeChar = new FakeActiveCharacterService("Bob", "Beta");

        var driver = new TestLogStreamDriver();
        var tracker = new PlayerAreaTracker(new AreaTransitionParser());
        var spy = new SpyAreaCalibration(Identity());
        var session = new SessionState();
        var settings = new LegolasSettings();
        var flow = new SurveyFlowController(session, settings);
        var gates = new ModuleGates();
        gates.For("legolas").Open();
        var motherlode = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(session),
            new FakePlayerPositionTracker(), new FakePlayerPinTracker());
        var svc = new PlayerLogIngestionService(
            driver, new PlayerLogParser(), tracker, spy, flow, session, motherlode,
            settings, gates,
            activeChar: activeChar, ingestionStore: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            driver.PushLive(LiveLine(MapFx, sequence: 10));
            await driver.DrainLocalPlayerAsync();
            await WaitUntil(() => session.Surveys.Count == 1, cts.Token);
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            _ = run;
            svc.Dispose();
        }

        // Final flush ran on shutdown — high-water should be persisted now.
        var reloaded = store.Load("Bob", "Beta");
        reloaded.PlayerLogHighWaterSequence.Should().BeGreaterOrEqualTo(10,
            "the handler advanced past Sequence 10 and the flush ran on shutdown");

        driver.Dispose();
    }

    // ---- helpers ----------------------------------------------------------

    private static async Task WaitUntil(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(15, ct);
        }
    }

    private static async Task Stop(
        Fixture f, Task run, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        try { await f.Service.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = run;
        f.Service.Dispose();
        f.Driver.Dispose();
    }

    private sealed class SpyAreaCalibration : IAreaCalibrationService
    {
        private readonly AreaCalibration? _cal;
        public SpyAreaCalibration(AreaCalibration? cal) => _cal = cal;

        public List<string> SelectedAreas { get; } = new();
        public void SelectArea(string areaKey) => SelectedAreas.Add(areaKey);

        public AreaCalibration? CurrentCalibration => _cal;
        public bool IsCurrentAreaCalibrated => _cal is not null;

        public string? CurrentAreaKey => null;
        public string? CurrentAreaFriendlyName => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences =>
            Array.Empty<CalibrationReference>();
        public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();
        public event EventHandler? Changed { add { } remove { } }
        public void OnAreaEntered(string areaFriendlyName) { }
        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements,
            double calibrationZoom = 1.0) => null;
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }

    /// <summary>
    /// Minimal IActiveCharacterService for the high-water tests — provides
    /// a stable Name/Server pair so PerCharacterStore writes land in a
    /// predictable directory.
    /// </summary>
    private sealed class FakeActiveCharacterService : Mithril.Shared.Character.IActiveCharacterService
    {
        public FakeActiveCharacterService(string name, string server)
        {
            ActiveCharacterName = name;
            ActiveServer = server;
        }

        public IReadOnlyList<Mithril.GameReports.CharacterSnapshot> Characters
            => Array.Empty<Mithril.GameReports.CharacterSnapshot>();
        public IReadOnlyList<Mithril.GameReports.ReportFileInfo> StorageReports
            => Array.Empty<Mithril.GameReports.ReportFileInfo>();
        public string? ActiveCharacterName { get; }
        public string? ActiveServer { get; }
        public Mithril.GameReports.CharacterSnapshot? ActiveCharacter => null;
        public Mithril.GameReports.ReportFileInfo? ActiveStorageReport => null;
        public Mithril.GameReports.StorageReport? ActiveStorageContents => null;
        public void SetActiveCharacter(string name, string server) { }
        public void Refresh() { }
        public event EventHandler? ActiveCharacterChanged { add { } remove { } }
        public event EventHandler? CharacterExportsChanged { add { } remove { } }
        public event EventHandler? StorageReportsChanged { add { } remove { } }
        public void Dispose() { }
    }
}
