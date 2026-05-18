using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Mithril.Shared.Reference;
using Xunit;

namespace Legolas.Tests.Services;

/// <summary>
/// #460: the view-agnostic cold-start pin-calibration driver. Mirrors the
/// Phase-4 pin-cal invariants but against the coordinator, not the (untouched,
/// redundant) standalone calibration VM.
/// </summary>
public class PinCalibrationCoordinatorTests
{
    private static (PinCalibrationCoordinator coord, FakeCalib calib) Build()
    {
        var calib = new FakeCalib();
        return (new PinCalibrationCoordinator(calib), calib);
    }

    [Fact]
    public void Disarmed_pins_are_dropped_so_area_entry_replay_cannot_leak()
    {
        var (coord, calib) = Build();
        // Bulk area-entry replay arrives BEFORE arming → must be ignored.
        calib.NotePinAdded(new WorldCoord(-521, 0, 368));
        calib.NotePinAdded(new WorldCoord(367, 0, 2798));

        coord.PendingCount.Should().Be(0);
        coord.PairClick(new PixelPoint(1, 1)); // nothing pending → no-op
        coord.PairedCount.Should().Be(0);
    }

    [Fact]
    public void Armed_pins_queue_and_pair_in_turn_order()
    {
        var (coord, calib) = Build();
        coord.Arm();

        var w0 = new WorldCoord(-521, 0, 368);
        var w1 = new WorldCoord(367, 0, 2798);
        var w2 = new WorldCoord(1145, 0, 1323);
        calib.NotePinAdded(w0);
        calib.NotePinAdded(w1);
        calib.NotePinAdded(w2);
        coord.PendingCount.Should().Be(3);
        coord.CanSolve.Should().BeFalse();

        // Oldest-first pairing, irrespective of anything else.
        coord.PairClick(new PixelPoint(10, 11));
        coord.PairClick(new PixelPoint(20, 22));
        coord.PairClick(new PixelPoint(30, 33));

        coord.PairedCount.Should().Be(3);
        coord.PendingCount.Should().Be(0);
        coord.CanSolve.Should().BeTrue();
        coord.PlacedMarkers.Should().HaveCount(3);

        coord.Solve().Should().NotBeNull();
        calib.LastPairs.Should().Equal(
            (w0, new PixelPoint(10, 11)),
            (w1, new PixelPoint(20, 22)),
            (w2, new PixelPoint(30, 33)));
        coord.IsArmed.Should().BeFalse("a successful solve disarms");
    }

    [Fact]
    public void Solve_below_three_pairs_is_null_and_does_not_call_service()
    {
        var (coord, calib) = Build();
        coord.Arm();
        calib.NotePinAdded(new WorldCoord(1, 0, 2));
        coord.PairClick(new PixelPoint(5, 5));

        coord.CanSolve.Should().BeFalse();
        coord.Solve().Should().BeNull();
        calib.LastPairs.Should().BeNull();
    }

    [Fact]
    public void Arm_clears_stale_state_then_Disarm_flushes()
    {
        var (coord, calib) = Build();
        coord.Arm();
        calib.NotePinAdded(new WorldCoord(1, 0, 2));
        coord.PairClick(new PixelPoint(5, 5));
        coord.PairedCount.Should().Be(1);

        coord.Arm(); // re-arm = fresh attempt
        coord.PairedCount.Should().Be(0);
        coord.PendingCount.Should().Be(0);
        coord.PlacedMarkers.Should().BeEmpty();

        calib.NotePinAdded(new WorldCoord(3, 0, 4));
        coord.PendingCount.Should().Be(1);
        coord.Disarm();
        coord.IsArmed.Should().BeFalse();
        coord.PendingCount.Should().Be(0);
        // Disarmed again → replay ignored.
        calib.NotePinAdded(new WorldCoord(7, 0, 8));
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

        public event EventHandler<WorldCoord>? PinAdded;
        public void NotePinAdded(WorldCoord world) => PinAdded?.Invoke(this, world);

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
