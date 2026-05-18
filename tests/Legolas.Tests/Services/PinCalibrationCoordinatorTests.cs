using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Mithril.GameState.Pins;
using Mithril.Shared.Reference;
using Xunit;
using PinShape = Mithril.GameState.Pins.PinShape;
using PinColor = Mithril.GameState.Pins.PinColor;

namespace Legolas.Tests.Services;

/// <summary>
/// #468: the cold-start pin-calibration driver now consumes the GameState
/// <see cref="IPlayerPinTracker"/>. Two routes, one solve — and the #454
/// label-agnostic rule is preserved (the solve only ever sees world↔pixel).
/// </summary>
public class PinCalibrationCoordinatorTests
{
    private static (PinCalibrationCoordinator coord, FakeCalib calib, FakePlayerPinTracker pins) Build()
    {
        var calib = new FakeCalib();
        var pins = new FakePlayerPinTracker();
        return (new PinCalibrationCoordinator(calib, pins), calib, pins);
    }

    // ---- Turn-order (freshly-dropped) route ----

    [Fact]
    public void Pre_arm_drops_are_ignored_so_replay_cannot_leak()
    {
        var (coord, _, pins) = Build();
        pins.Add(-521, 368);
        pins.Add(367, 2798);

        coord.PendingCount.Should().Be(0);
        coord.PairClick(new PixelPoint(1, 1));
        coord.PairedCount.Should().Be(0);
    }

    [Fact]
    public void Armed_fresh_drops_queue_and_pair_in_turn_order()
    {
        var (coord, calib, pins) = Build();
        coord.Arm();

        var w0 = pins.Add(-521, 368);
        var w1 = pins.Add(367, 2798);
        var w2 = pins.Add(1145, 1323);
        coord.PendingCount.Should().Be(3);
        coord.CanSolve.Should().BeFalse();

        coord.PairClick(new PixelPoint(10, 11));
        coord.PairClick(new PixelPoint(20, 22));
        coord.PairClick(new PixelPoint(30, 33));

        coord.PairedCount.Should().Be(3);
        coord.PendingCount.Should().Be(0);
        coord.CanSolve.Should().BeTrue();
        coord.PlacedMarkers.Should().HaveCount(3);

        coord.Solve().Should().NotBeNull();
        calib.LastPairs.Should().Equal(
            (new WorldCoord(w0.X, 0, w0.Z), new PixelPoint(10, 11)),
            (new WorldCoord(w1.X, 0, w1.Z), new PixelPoint(20, 22)),
            (new WorldCoord(w2.X, 0, w2.Z), new PixelPoint(30, 33)));
        coord.IsArmed.Should().BeFalse("a successful solve disarms");
    }

    // ---- Existing-pins route ----

    [Fact]
    public void Existing_pins_are_offered_and_pair_by_deliberate_selection()
    {
        var (coord, calib, pins) = Build();
        var a = FakePlayerPinTracker.Pin(10, 20, "Fire Magic 25", PinShape.Dot, PinColor.Red);
        var b = FakePlayerPinTracker.Pin(30, 40, "North", PinShape.Square, PinColor.White);
        var c = FakePlayerPinTracker.Pin(50, 60, "");
        pins.SeedExisting(a, b, c);

        coord.Arm();
        coord.HasUsableExistingPins.Should().BeTrue();
        coord.ExistingPins.Should().HaveCount(3);
        // UX identity is derivable (used only to help the human pick).
        a.Appearance.Should().Be("red dot");

        coord.SelectedExistingPin = b;
        coord.PairClick(new PixelPoint(1, 1));
        coord.SelectedExistingPin = a;
        coord.PairClick(new PixelPoint(2, 2));
        coord.SelectedExistingPin = c;
        coord.PairClick(new PixelPoint(3, 3));

        coord.PairedCount.Should().Be(3);
        coord.Solve().Should().NotBeNull();
        // Pairs follow the user's selection order — not list/turn order — and
        // carry only world↔pixel (no label/colour reaches the solver).
        calib.LastPairs.Should().Equal(
            (new WorldCoord(30, 0, 40), new PixelPoint(1, 1)),
            (new WorldCoord(10, 0, 20), new PixelPoint(2, 2)),
            (new WorldCoord(50, 0, 60), new PixelPoint(3, 3)));
    }

    [Fact]
    public void Snapshot_seeds_existing_pins_before_arming()
    {
        var calib = new FakeCalib();
        var pins = new FakePlayerPinTracker();
        pins.SeedExisting(FakePlayerPinTracker.Pin(1, 2), FakePlayerPinTracker.Pin(3, 4));

        var coord = new PinCalibrationCoordinator(calib, pins); // Subscribe → Snapshot
        coord.ExistingPins.Should().HaveCount(2);
        coord.HasUsableExistingPins.Should().BeFalse("only 2 < 3");
    }

    [Fact]
    public void Duplicate_world_point_is_rejected_to_keep_the_solve_conditioned()
    {
        var (coord, _, _) = Build();
        var p = FakePlayerPinTracker.Pin(10, 20, "A");
        coord.Arm();
        coord.SelectedExistingPin = p;
        coord.PairClick(new PixelPoint(1, 1));
        coord.SelectedExistingPin = p; // same pin again
        coord.PairClick(new PixelPoint(9, 9));
        coord.PairedCount.Should().Be(1);
    }

    // ---- Shared invariants ----

    [Fact]
    public void Solve_below_three_pairs_is_null_and_does_not_call_service()
    {
        var (coord, calib, pins) = Build();
        coord.Arm();
        pins.Add(1, 2);
        coord.PairClick(new PixelPoint(5, 5));

        coord.CanSolve.Should().BeFalse();
        coord.Solve().Should().BeNull();
        calib.LastPairs.Should().BeNull();
    }

    [Fact]
    public void Arm_clears_stale_state_then_Disarm_flushes()
    {
        var (coord, _, pins) = Build();
        coord.Arm();
        pins.Add(1, 2);
        coord.PairClick(new PixelPoint(5, 5));
        coord.PairedCount.Should().Be(1);

        coord.Arm();
        coord.PairedCount.Should().Be(0);
        coord.PendingCount.Should().Be(0);
        coord.PlacedMarkers.Should().BeEmpty();

        pins.Add(3, 4);
        coord.PendingCount.Should().Be(1);
        coord.Disarm();
        coord.IsArmed.Should().BeFalse();
        coord.PendingCount.Should().Be(0);
        pins.Add(7, 8);
        coord.PendingCount.Should().Be(0);
    }

    private sealed class FakeCalib : IAreaCalibrationService
    {
        public List<(WorldCoord, PixelPoint)>? LastPairs { get; private set; }

        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements, double calibrationZoom = 1.0)
        {
            LastPairs = placements.Select(p => (p.World, p.Pixel)).ToList();
            return new AreaCalibration(1, 0, 0, 0, placements.Count, 0);
        }

        public string? CurrentAreaKey => "AreaTest";
        public string? CurrentAreaFriendlyName => "Test";
        public bool IsCurrentAreaCalibrated => false;
        public AreaCalibration? CurrentCalibration => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Array.Empty<CalibrationReference>();
        public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();
        public event EventHandler? Changed { add { } remove { } }
        public void OnAreaEntered(string areaFriendlyName) { }
        public void SelectArea(string areaKey) { }
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }
}
