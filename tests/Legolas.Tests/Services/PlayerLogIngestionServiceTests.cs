using System.IO;
using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Xunit;

namespace Legolas.Tests.Services;

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

    private static (PlayerLogIngestionService svc, ScriptedStream stream, SpyAreaCalibration spy,
        SessionState session) Build(AreaCalibration? calibration = null, GameConfig? config = null)
    {
        var stream = new ScriptedStream();
        var tracker = new PlayerAreaTracker(new AreaTransitionParser());
        var spy = new SpyAreaCalibration(calibration);
        var session = new SessionState();
        var settings = new LegolasSettings();
        var gates = new ModuleGates();
        gates.For("legolas").Open();
        var svc = new PlayerLogIngestionService(
            stream, new PlayerLogParser(), tracker, spy, session, settings, gates,
            config ?? new GameConfig());
        return (svc, stream, spy, session);
    }

    // ---- area→calibration bridge (Phase 2) -------------------------------

    [Fact]
    public async Task Area_load_line_applies_that_area_calibration_once()
    {
        var (svc, stream, spy, _) = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push("[08:25:13] LOADING LEVEL AreaEltibule");
            stream.Push("[08:30:00] LOADING LEVEL AreaEltibule");
            await stream.WaitForDrainAsync(cts.Token);
            spy.SelectedAreas.Should().Equal("AreaEltibule");
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task Area_change_applies_each_distinct_area_in_order()
    {
        var (svc, stream, spy, _) = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push("[08:25:13] LOADING LEVEL AreaEltibule");
            stream.Push("[08:57:37] LOADING LEVEL AreaSerbule");
            await stream.WaitForDrainAsync(cts.Token);
            spy.SelectedAreas.Should().Equal("AreaEltibule", "AreaSerbule");
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task ChooseCharacter_resets_latch_so_same_area_re_applies()
    {
        var (svc, stream, spy, _) = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push("[08:25:13] LOADING LEVEL AreaEltibule");
            stream.Push("[08:57:00] LOADING LEVEL ChooseCharacter");
            stream.Push("[09:00:39] LOADING LEVEL AreaEltibule");
            await stream.WaitForDrainAsync(cts.Token);
            spy.SelectedAreas.Should().Equal("AreaEltibule", "AreaEltibule");
        }
        finally { await Stop(svc, run, cts); }
    }

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

        var (svc, _, spy, _) = Build(config: new GameConfig { GameRoot = _tempDir });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            await WaitUntil(() => spy.SelectedAreas.Count > 0, cts.Token);
            spy.SelectedAreas.Should().Equal("AreaEltibule");
        }
        finally { await Stop(svc, run, cts); }
    }

    // ---- absolute ProcessMapFx placement (Phase 3) -----------------------

    [Fact]
    public async Task Calibrated_area_places_one_absolute_pin_at_projected_pixel()
    {
        var (svc, stream, _, session) = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(MapFx);
            await stream.WaitForDrainAsync(cts.Token);
            // Wait on the terminal signal HandleMapTarget sets last, so the
            // SelectedSurvey/IsInventoryVisible writes aren't raced.
            await WaitUntil(() => session.LastLogEvent?.Contains("placed (absolute)") == true, cts.Token);

            session.Surveys.Should().HaveCount(1);
            var pin = session.Surveys[0];
            pin.Name.Should().Be("Good Metal Slab");        // " is here" stripped
            pin.Model.World.Should().Be(new WorldCoord(1236.00, 38.17, 2528.00));
            // Identity calibration (s=1, θ=0, origin=0): px=X, py=-Z — screen-Y
            // grows down while world-north (Z) grows up, so Z is negated.
            pin.Model.PixelPos.Should().Be(new PixelPoint(1236.00, -2528.00));
            session.SelectedSurvey.Should().BeSameAs(pin);
            session.IsInventoryVisible.Should().BeTrue();
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task Duplicate_world_coord_within_radius_does_not_stack()
    {
        var (svc, stream, _, session) = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(MapFx);
            // Same node re-surveyed re-emits the identical (X,Z); only the
            // relative msg text drifts (67m → 66m).
            stream.Push(MapFx.Replace("67m west and 1181m", "66m west and 1180m"));
            await stream.WaitForDrainAsync(cts.Token);
            await WaitUntil(() => session.Surveys.Count >= 1, cts.Token);

            session.Surveys.Should().HaveCount(1);
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task Distinct_targets_place_separate_pins()
    {
        var (svc, stream, _, session) = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(MapFx);
            stream.Push(
                "[08:33:17] LocalPlayer: ProcessMapFx((1666.00, 36.95, 2620.00), 25, 1, " +
                "\"Good Metal Slab is here\", ImportantInfo, \"The Good Metal Slab is 604m west and 1073m south.\")");
            await stream.WaitForDrainAsync(cts.Token);
            await WaitUntil(() => session.Surveys.Count == 2, cts.Token);

            session.Surveys.Should().HaveCount(2);
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task Uncalibrated_area_does_not_place_and_reports_diagnostic()
    {
        var (svc, stream, _, session) = Build(calibration: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(MapFx);
            await stream.WaitForDrainAsync(cts.Token);
            await WaitUntil(() => session.LastLogEvent?.Contains("not calibrated") == true, cts.Token);

            session.Surveys.Should().BeEmpty();
            session.LastLogEvent.Should().Contain("not calibrated");
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task Motherlode_mode_ignores_absolute_targets()
    {
        var (svc, stream, _, session) = Build(calibration: Identity());
        session.Mode = SessionMode.Motherlode;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(MapFx);
            await stream.WaitForDrainAsync(cts.Token);
            await WaitUntil(() => session.LastLogEvent?.Contains("Motherlode") == true, cts.Token);

            session.Surveys.Should().BeEmpty();
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task ProcessMapPin_lines_are_ignored_by_this_service()
    {
        // Map-pin lifecycle is GameState-owned now (#468); the Legolas
        // Player.log consumer only handles ProcessMapFx targets.
        var (svc, stream, _, session) = Build(calibration: Identity());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push("[08:32:20] LocalPlayer: ProcessMapPinAdd(1, 0, 0, (1145.39, 0.00, 1323.40), \"Calib 1\")");
            stream.Push("[08:32:21] LocalPlayer: ProcessMapPinRemove(1, 0, 0, (1145.39, 0.00, 1323.40), \"Calib 1\")");
            await stream.WaitForDrainAsync(cts.Token);

            session.Surveys.Should().BeEmpty();
        }
        finally { await Stop(svc, run, cts); }
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
        PlayerLogIngestionService svc, Task run, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = run;
        svc.Dispose();
    }

    /// <summary>
    /// Captures <see cref="IAreaCalibrationService.SelectArea"/> calls and
    /// reports a fixed <see cref="AreaCalibration"/> (or none) as the current
    /// area's calibration.
    /// </summary>
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

    private sealed class ScriptedStream : IPlayerLogStream
    {
        private readonly Channel<RawLogLine> _channel = Channel.CreateUnbounded<RawLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public void Push(string line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(new RawLogLine(DateTime.UtcNow, line));
        }

        public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);

        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var line))
                {
                    yield return line;
                    if (Interlocked.Decrement(ref _pending) == 0)
                        _drained.TrySetResult();
                }
            }
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
