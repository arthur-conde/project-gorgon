using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Mithril.Shared.Reference;
using Xunit;
using PinShape = Mithril.GameState.Pins.PinShape;
using PinColor = Mithril.GameState.Pins.PinColor;

namespace Legolas.Tests.Services;

/// <summary>
/// #477 Part A: the guided two-phase correctable walkthrough. One model, two
/// phases; spread-suggested pairing with skip/override; non-persisting residual
/// preview; correction (select/drag/nudge); terminal Confirm. The #454
/// label-agnostic rule still holds — only (world↔pixel) reaches the solver.
/// </summary>
public class PinCalibrationCoordinatorTests
{
    private static (PinCalibrationCoordinator coord, FakeCalib calib, FakePlayerPinTracker pins, LegolasSettings settings) Build()
    {
        var calib = new FakeCalib();
        var pins = new FakePlayerPinTracker();
        var settings = new LegolasSettings();
        return (new PinCalibrationCoordinator(calib, pins, settings), calib, pins, settings);
    }

    // A well-conditioned scale-only transform: pixel = (100 + 2X, 100 - 2Z),
    // i.e. AreaCalibration{scale=2, origin=(100,100), no rotation, north=+Z}.
    private static PixelPoint Project(double x, double z) => new(100 + 2 * x, 100 - 2 * z);

    // Pair every remaining pin at its own perfect projection, in whatever
    // spread order the coordinator suggests (the no-correction baseline).
    private static void PairAllPerfectly(PinCalibrationCoordinator coord)
    {
        while (coord.SuggestedPin is { } s)
            coord.PairClick(Project(s.X, s.Z));
    }

    // ---- Phase model ----

    [Fact]
    public void Arm_starts_in_Drop_when_under_three_pins_else_Pair()
    {
        var (coord, _, pins, _) = Build();
        coord.Arm();
        coord.Phase.Should().Be(CalibrationPhase.Drop, "no pins yet");

        pins.SeedExisting(
            FakePlayerPinTracker.Pin(1, 2), FakePlayerPinTracker.Pin(3, 4), FakePlayerPinTracker.Pin(5, 6));
        coord.Arm();
        coord.Phase.Should().Be(CalibrationPhase.Pair, "≥3 usable pins → skip Drop");
    }

    [Fact]
    public void Phase_toggle_flips_capture_intent_and_is_idempotent()
    {
        var (coord, _, _, _) = Build();
        coord.Arm(); // Drop
        coord.IsDropping.Should().BeTrue();
        coord.IsPairing.Should().BeFalse();

        coord.TogglePhase();
        coord.Phase.Should().Be(CalibrationPhase.Pair);
        coord.IsPairing.Should().BeTrue();
        coord.IsDropping.Should().BeFalse();

        coord.TogglePhase();
        coord.Phase.Should().Be(CalibrationPhase.Drop, "toggle is a clean flip-back");
    }

    [Fact]
    public void Phase1_live_count_reflects_PinSetChanged_Added()
    {
        var (coord, _, pins, _) = Build();
        coord.Arm();
        coord.PinsAvailable.Should().Be(0);
        pins.Add(1, 2);
        pins.Add(3, 4);
        coord.PinsAvailable.Should().Be(2);
        coord.HasUsablePins.Should().BeFalse();
        pins.Add(5, 6);
        coord.HasUsablePins.Should().BeTrue();
    }

    // ---- Spread suggestion + skip/override ----

    [Fact]
    public void Suggestion_is_the_farthest_unpaired_pin_from_already_paired()
    {
        var (coord, _, pins, _) = Build();
        var near = FakePlayerPinTracker.Pin(11, 10);
        var far = FakePlayerPinTracker.Pin(900, 900);
        pins.SeedExisting(FakePlayerPinTracker.Pin(10, 10), near, far);
        coord.Arm(); // Pair (3 pins)

        // First suggestion (no pairs yet) is deterministic: list head.
        coord.SuggestedPin!.X.Should().Be(10);
        coord.PairClick(Project(10, 10));

        // Now the farthest-from-(10,10) unpaired pin wins.
        coord.SuggestedPin.Should().Be(far);
    }

    [Fact]
    public void Skip_defers_the_pin_without_recording_a_pair()
    {
        var (coord, _, pins, _) = Build();
        var a = FakePlayerPinTracker.Pin(10, 10);
        var b = FakePlayerPinTracker.Pin(20, 20);
        var c = FakePlayerPinTracker.Pin(30, 30);
        pins.SeedExisting(a, b, c);
        coord.Arm();

        var first = coord.SuggestedPin;
        coord.SkipSuggestion();
        coord.PairedCount.Should().Be(0, "skip records nothing");
        coord.SuggestedPin.Should().NotBe(first, "a different pin is offered");
    }

    [Fact]
    public void Override_pin_takes_precedence_over_the_spread_suggestion()
    {
        var (coord, _, pins, _) = Build();
        var a = FakePlayerPinTracker.Pin(10, 10);
        var b = FakePlayerPinTracker.Pin(20, 20);
        var c = FakePlayerPinTracker.Pin(30, 30);
        pins.SeedExisting(a, b, c);
        coord.Arm();

        coord.OverridePin = c;
        coord.SuggestedPin.Should().Be(c);
        coord.PairClick(Project(30, 30));
        // Solver only ever sees (world↔pixel) — verified on Confirm below.
        coord.PairedCount.Should().Be(1);
        coord.OverridePin.Should().BeNull("cleared once paired");
    }

    // ---- Pairing (implicit advance) + finalize floor ----

    [Fact]
    public void Pairing_is_implicit_advance_and_under_three_cannot_finalize()
    {
        var (coord, calib, pins, _) = Build();
        pins.SeedExisting(
            FakePlayerPinTracker.Pin(10, 10),
            FakePlayerPinTracker.Pin(50, 60),
            FakePlayerPinTracker.Pin(90, 20));
        coord.Arm();

        coord.PairClick(Project(10, 10));
        coord.PairClick(Project(50, 60));
        coord.PairedCount.Should().Be(2);
        coord.CanConfirm.Should().BeFalse();
        coord.Confirm().Should().BeNull("≥3 pairs is a hard floor");
        coord.ConfirmAnyway().Should().BeNull();
        calib.LastPairs.Should().BeNull("nothing persisted below the floor");

        coord.PairClick(Project(90, 20));
        coord.CanConfirm.Should().BeTrue();
    }

    [Fact]
    public void Duplicate_world_point_is_rejected_to_keep_the_solve_conditioned()
    {
        var (coord, _, pins, _) = Build();
        var p = FakePlayerPinTracker.Pin(10, 20, "A");
        pins.SeedExisting(p);
        coord.Arm();
        coord.TogglePhase(); // 1 pin → entered in Drop; move to Pair
        coord.OverridePin = p;
        coord.PairClick(new PixelPoint(1, 1));
        coord.OverridePin = p; // same pin again
        coord.PairClick(new PixelPoint(9, 9));
        coord.PairedCount.Should().Be(1);
    }

    // ---- Correction (select / drag / nudge) ----

    [Fact]
    public void Hit_test_selects_nearest_marker_and_drag_moves_only_that_pixel()
    {
        var (coord, calib, pins, _) = Build();
        pins.SeedExisting(
            FakePlayerPinTracker.Pin(10, 10),
            FakePlayerPinTracker.Pin(50, 60),
            FakePlayerPinTracker.Pin(90, 20));
        coord.Arm();
        PairAllPerfectly(coord); // marker[0] is the first-suggested pin = (10,10)

        var other = coord.PlacedMarkers[1];
        var otherPixel = other.Pixel;

        coord.TrySelectMarkerAt(new PixelPoint(121, 81), radius: 14).Should().BeTrue();
        coord.SelectedMarker!.PairIndex.Should().Be(0);
        coord.SelectedMarker.Pixel.Should().Be(Project(10, 10));

        coord.DragSelectedTo(new PixelPoint(500, 500));
        coord.PlacedMarkers[0].Pixel.Should().Be(new PixelPoint(500, 500));
        other.Pixel.Should().Be(otherPixel, "other markers are untouched");

        coord.NudgeSelected(3, -2);
        coord.PlacedMarkers[0].Pixel.Should().Be(new PixelPoint(503, 498));

        // The world half is never mutated — Confirm proves the pairs carry the
        // dragged pixel against the ORIGINAL world coords.
        coord.Confirm(); // residual high after the drag → gated; force anyway
        coord.ConfirmAnyway();
        calib.LastPairs!.Should().Contain(p =>
            p.Item1 == new WorldCoord(10, 0, 10) && p.Item2 == new PixelPoint(503, 498));
        calib.LastPairs!.Should().Contain(p =>
            p.Item1 == new WorldCoord(50, 0, 60) && p.Item2 == Project(50, 60));
    }

    [Fact]
    public void Miss_returns_false_so_the_click_pairs_instead()
    {
        var (coord, _, pins, _) = Build();
        pins.SeedExisting(
            FakePlayerPinTracker.Pin(10, 10),
            FakePlayerPinTracker.Pin(50, 60),
            FakePlayerPinTracker.Pin(90, 20));
        coord.Arm();
        coord.PairClick(Project(10, 10));
        coord.TrySelectMarkerAt(new PixelPoint(9999, 9999), radius: 14).Should().BeFalse();
    }

    // ---- Non-persisting residual preview ----

    [Fact]
    public void Residual_preview_is_non_persisting_and_equals_a_direct_solve()
    {
        var (coord, calib, pins, _) = Build();
        pins.SeedExisting(
            FakePlayerPinTracker.Pin(10, 10),
            FakePlayerPinTracker.Pin(50, 60),
            FakePlayerPinTracker.Pin(90, 20));
        coord.Arm();

        coord.PreviewResidual.Should().BeNull("under 3 pairs");
        PairAllPerfectly(coord);

        coord.PreviewResidual.Should().NotBeNull();
        calib.LastPairs.Should().BeNull("preview must not persist");
        calib.ChangedCount.Should().Be(0, "preview must not fire Changed");

        // Equals a direct solve of the same (world↔pixel) pairs. Residual is
        // order-independent, so the suggestion order doesn't matter.
        var refs = new[]
        {
            new LandmarkCalibrationSolver.Reference(10, 10, Project(10, 10)),
            new LandmarkCalibrationSolver.Reference(50, 60, Project(50, 60)),
            new LandmarkCalibrationSolver.Reference(90, 20, Project(90, 20)),
        };
        var direct = LandmarkCalibrationSolver.Solve(refs)!.ResidualPixels;
        coord.PreviewResidual!.Value.Should().BeApproximately(direct, 1e-6);
    }

    [Fact]
    public void Confirm_gate_flips_at_the_configured_threshold_and_finish_anyway_persists()
    {
        var (coord, calib, pins, settings) = Build();
        pins.SeedExisting(
            FakePlayerPinTracker.Pin(10, 10),
            FakePlayerPinTracker.Pin(50, 60),
            FakePlayerPinTracker.Pin(90, 20));
        coord.Arm();

        coord.PairClick(Project(10, 10));
        coord.PairClick(Project(50, 60));
        // One badly misplaced click → a large residual.
        coord.PairClick(new PixelPoint(Project(90, 20).X + 400, Project(90, 20).Y));

        coord.IsResidualGood.Should().BeFalse();
        coord.Confirm().Should().BeNull("gated on a good residual");
        calib.LastPairs.Should().BeNull();

        // Raise the threshold above the residual → the gate opens.
        settings.CalibrationGoodResidualPx = coord.PreviewResidual!.Value + 10;
        coord.IsResidualGood.Should().BeTrue();
        coord.Confirm().Should().NotBeNull();
        coord.IsArmed.Should().BeFalse("a successful confirm disarms");
    }

    [Fact]
    public void No_correction_run_solves_equivalently_and_only_Confirm_persists()
    {
        var (coord, calib, pins, _) = Build();
        var a = FakePlayerPinTracker.Pin(10, 10);
        var b = FakePlayerPinTracker.Pin(50, 60);
        var c = FakePlayerPinTracker.Pin(90, 20);
        pins.SeedExisting(a, b, c);
        coord.Arm();

        // Click each named dot precisely (the regression baseline).
        while (coord.SuggestedPin is { } s)
            coord.PairClick(Project(s.X, s.Z));

        calib.LastPairs.Should().BeNull("only Confirm persists");
        coord.Confirm().Should().NotBeNull();
        // Pure (world↔pixel); no label/colour/shape ever reaches the solver.
        calib.LastPairs.Should().BeEquivalentTo(new[]
        {
            (new WorldCoord(10, 0, 10), Project(10, 10)),
            (new WorldCoord(50, 0, 60), Project(50, 60)),
            (new WorldCoord(90, 0, 20), Project(90, 20)),
        });
    }

    [Fact]
    public void Arm_clears_stale_state()
    {
        var (coord, _, pins, _) = Build();
        pins.SeedExisting(
            FakePlayerPinTracker.Pin(10, 10),
            FakePlayerPinTracker.Pin(50, 60),
            FakePlayerPinTracker.Pin(90, 20));
        coord.Arm();
        coord.PairClick(Project(10, 10));
        coord.PairedCount.Should().Be(1);

        coord.Arm();
        coord.PairedCount.Should().Be(0);
        coord.PlacedMarkers.Should().BeEmpty();
        coord.PreviewResidual.Should().BeNull();
        coord.SelectedMarker.Should().BeNull();
    }

    private sealed class FakeCalib : IAreaCalibrationService
    {
        public List<(WorldCoord, PixelPoint)>? LastPairs { get; private set; }
        public int ChangedCount { get; private set; }

        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements, double calibrationZoom = 1.0)
        {
            LastPairs = placements.Select(p => (p.World, p.Pixel)).ToList();
            ChangedCount++;
            Changed?.Invoke(this, EventArgs.Empty);
            return new AreaCalibration(1, 0, 0, 0, placements.Count, 0);
        }

        public string? CurrentAreaKey => "AreaTest";
        public string? CurrentAreaFriendlyName => "Test";
        public bool IsCurrentAreaCalibrated => false;
        public AreaCalibration? CurrentCalibration => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Array.Empty<CalibrationReference>();
        public IReadOnlyList<AreaEntry> AllAreas => Array.Empty<AreaEntry>();
        public event EventHandler? Changed;
        public void SelectArea(string areaKey) { }
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }
}
