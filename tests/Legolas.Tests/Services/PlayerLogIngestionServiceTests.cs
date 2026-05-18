using System.IO;
using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
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

    private static (PlayerLogIngestionService svc, ScriptedStream stream, SpyAreaCalibration spy)
        Build(GameConfig? config = null)
    {
        var stream = new ScriptedStream();
        var tracker = new PlayerAreaTracker(new AreaTransitionParser());
        var spy = new SpyAreaCalibration();
        var gates = new ModuleGates();
        gates.For("legolas").Open();
        var svc = new PlayerLogIngestionService(
            stream, tracker, spy, gates, config ?? new GameConfig());
        return (svc, stream, spy);
    }

    [Fact]
    public async Task Area_load_line_applies_that_area_calibration_once()
    {
        var (svc, stream, spy) = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push("[08:25:13] LOADING LEVEL AreaEltibule");
            await stream.WaitForDrainAsync(cts.Token);

            spy.SelectedAreas.Should().Equal("AreaEltibule");
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task Duplicate_area_line_does_not_re_apply()
    {
        var (svc, stream, spy) = Build();
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
        var (svc, stream, spy) = Build();
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
    public async Task Unrelated_line_is_a_noop()
    {
        var (svc, stream, spy) = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push("[08:25:39] LocalPlayer: ProcessMapFx((1236.00, 38.17, 2528.00), 25, 1, \"x\", ImportantInfo, \"y\")");
            await stream.WaitForDrainAsync(cts.Token);

            spy.SelectedAreas.Should().BeEmpty();
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task ChooseCharacter_resets_latch_so_same_area_re_applies()
    {
        var (svc, stream, spy) = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            stream.Push("[08:25:13] LOADING LEVEL AreaEltibule");
            stream.Push("[08:57:00] LOADING LEVEL ChooseCharacter");
            stream.Push("[09:00:39] LOADING LEVEL AreaEltibule");
            await stream.WaitForDrainAsync(cts.Token);

            // Re-entry after character-select re-applies — defensive against an
            // intervening projector reset.
            spy.SelectedAreas.Should().Equal("AreaEltibule", "AreaEltibule");
        }
        finally { await Stop(svc, run, cts); }
    }

    [Fact]
    public async Task Startup_seed_applies_current_area_before_live_lines()
    {
        var logPath = Path.Combine(_tempDir, "Player.log");
        // BOM-less UTF-8 — real Player.log carries no BOM (mirrors the
        // PlayerAreaTracker seed-test convention).
        File.WriteAllText(
            logPath,
            "LocalPlayer: ProcessAddItem(Apple(1), -1, True)\n" +
            "LOADING LEVEL AreaEltibule\n" +
            "LocalPlayer: ProcessAddPlayer(...)\n",
            new UTF8Encoding(false));
        var config = new GameConfig { GameRoot = _tempDir };

        var (svc, stream, spy) = Build(config);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = svc.StartAsync(cts.Token);
        try
        {
            // No live line pushed — the seed alone must have applied the area.
            await WaitUntil(() => spy.SelectedAreas.Count > 0, cts.Token);
            spy.SelectedAreas.Should().Equal("AreaEltibule");
        }
        finally { await Stop(svc, run, cts); }
    }

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
    /// Captures <see cref="IAreaCalibrationService.SelectArea"/> invocations
    /// in order. Every other member is an inert stub — Phase 2 only exercises
    /// the area→calibration bridge.
    /// </summary>
    private sealed class SpyAreaCalibration : IAreaCalibrationService
    {
        public List<string> SelectedAreas { get; } = new();

        public void SelectArea(string areaKey) => SelectedAreas.Add(areaKey);

        public string? CurrentAreaKey => null;
        public string? CurrentAreaFriendlyName => null;
        public bool IsCurrentAreaCalibrated => false;
        public AreaCalibration? CurrentCalibration => null;
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
