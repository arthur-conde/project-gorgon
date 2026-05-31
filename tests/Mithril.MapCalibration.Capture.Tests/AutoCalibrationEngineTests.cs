using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Capture.Tests;

/// <summary>
/// Task 24 (#914): the orchestrator. Persist via SaveUserRefinement (Source =
/// AutoCapture) ONLY on gate-accept; otherwise keep the prior calibration and
/// report a reason. Short-circuit (no capture, no solve) on the §11 conditions:
/// no current area, no bbox, PG not foreground, null base texture.
/// </summary>
public sealed class AutoCalibrationEngineTests
{
    private const string Area = "AreaEltibule";

    [Fact]
    public async Task Persists_with_AutoCapture_source_on_accept()
    {
        var svc = new FakeCalibrationService();
        var h = new EngineHarness { Solve = Accepted(residual: 0.65, inliers: 5), Service = svc };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeTrue();
        outcome.AreaKey.Should().Be(Area);
        svc.Saved.Should().ContainKey(Area);
        svc.Saved[Area].Source.Should().Be(CalibrationSource.AutoCapture);
    }

    [Fact]
    public async Task Keeps_prior_calibration_when_the_gate_rejects()
    {
        var svc = new FakeCalibrationService();
        svc.Seed(Area, SomeBaseline());
        var h = new EngineHarness { Solve = Rejected("residual 25.00 px exceeds threshold 12.00 px"), Service = svc };

        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("residual");
        svc.Saved.Should().NotContainKey(Area); // prior untouched (no Save call)
    }

    [Fact]
    public async Task No_bbox_short_circuits_without_capturing()
    {
        var h = new EngineHarness { Bbox = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("bbox");
        h.Capture.Called.Should().BeFalse();
        h.Solver.Called.Should().BeFalse();
    }

    [Fact]
    public async Task No_current_area_short_circuits()
    {
        var h = new EngineHarness { CurrentArea = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("not in-world");
        h.Capture.Called.Should().BeFalse();
    }

    [Fact]
    public async Task PG_not_foreground_short_circuits_without_capturing()
    {
        var h = new EngineHarness { GameWindow = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("Project Gorgon");
        h.Capture.Called.Should().BeFalse();
    }

    [Fact]
    public async Task Null_base_texture_fails_soft_without_solving()
    {
        var h = new EngineHarness { BaseTexture = null };
        var outcome = await h.Engine().TryCalibrateCurrentAreaAsync(default);

        outcome.Persisted.Should().BeFalse();
        outcome.RejectReason.Should().Contain("map assets");
        h.Solver.Called.Should().BeFalse("no base texture → never reach the solver");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static CalibrationSolveResult Accepted(double residual, int inliers) =>
        new(new AreaCalibration(1.2, 0.1, 100, 100, inliers, residual), inliers, null);

    private static CalibrationSolveResult Rejected(string reason) => new(null, 0, reason);

    private static AreaCalibration SomeBaseline() =>
        new(1.0, 0, 50, 50, 4, 3.0) { Source = CalibrationSource.BundledBaseline };

    /// <summary>
    /// Mutable harness: each property has a sensible "happy path" default; a test
    /// overrides exactly the one input it exercises. Setting a reference-type
    /// property to <c>null</c> models the absence of that input.
    /// </summary>
    private sealed class EngineHarness
    {
        public string? CurrentArea { get; init; } = Area;
        public CaptureRect? Bbox { get; init; } = new CaptureRect(0, 0, 64, 64);
        public GameWindow? GameWindow { get; init; } = new GameWindow(1, new CaptureRect(0, 0, 1920, 1080));
        public GrayImage? BaseTexture { get; init; } = new GrayImage(64, 64, new byte[64 * 64]);
        public CalibrationSolveResult Solve { get; init; } = new(new AreaCalibration(1, 0, 0, 0, 6, 0.5), 6, null);
        public FakeCalibrationService Service { get; init; } = new();

        public SpyCapture Capture { get; } = new(new GrayImage(64, 64, new byte[64 * 64]));
        public SpySolver Solver { get; private set; } = null!;

        public AutoCalibrationEngine Engine()
        {
            Solver = new SpySolver(Solve);
            return new AutoCalibrationEngine(
                new FakeAreaState(CurrentArea),
                new FakeWindowLocator(GameWindow),
                new FakeRegionProvider(Bbox),
                Capture,
                new FakeRefiner(new MapRect(0, 0, 64, 64, 64, 64)),
                new FakeBaseTextureProvider(BaseTexture),
                new FakeAreaRefs(new[] { new LandmarkReference("landmark_npc", "x", new WorldCoord(1, 0, 1)) }),
                Solver,
                IconTemplateSet.Empty,
                Service,
                logger: null);
        }
    }
}
