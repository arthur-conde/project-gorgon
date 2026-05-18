using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.GameState.Movement;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #476 — Survey player-GPS restored from <see cref="IPlayerPositionTracker"/>:
/// the route start, the rendered marker, and the pre-first-collection segment,
/// projected through the area calibration. Asserts the regression is fixed and
/// the degrade-silently fallback (uncalibrated area / no fix) is preserved.
/// </summary>
public class SurveyPlayerGpsTests
{
    private sealed class CapturingOptimizer : IRouteOptimizer
    {
        public PixelPoint? LastStart { get; private set; }
        public IReadOnlyList<int> Optimize(PixelPoint start, IReadOnlyList<PixelPoint> points,
            CancellationToken cancellationToken = default)
        {
            LastStart = start;
            return Enumerable.Range(0, points.Count).ToList();
        }
    }

    // Scale 1, no rotation, origin (0,0): ProjectWorld(x,_,z) → (x, -z).
    private static readonly AreaCalibration Calib = new(
        Scale: 1.0, RotationRadians: 0.0, OriginX: 0.0, OriginY: 0.0,
        ReferenceCount: 3, ResidualPixels: 0.5);

    private static (SessionState session, MapOverlayViewModel map,
        FakePlayerPositionTracker tracker, FakeAreaCalibrationService areaCal, CapturingOptimizer opt)
        BuildSut(bool calibrated, double px = 40, double pz = -25,
            PlayerPositionSource source = PlayerPositionSource.Spawn)
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var opt = new CapturingOptimizer();
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var tracker = new FakePlayerPositionTracker();
        tracker.Seed(px, 0, pz, source);
        var areaCal = new FakeAreaCalibrationService();
        if (calibrated) areaCal.SetCalibration(Calib);

        var map = new MapOverlayViewModel(
            session, projector, opt, surveyFlow, brushes, settings,
            pinCalibration: null, positionTracker: tracker, areaCalibration: areaCal);
        return (session, map, tracker, areaCal, opt);
    }

    private static SurveyItemViewModel SeedSurveyAt(SessionState session, string name,
        double x, double y, int routeOrder)
    {
        var s = Survey.Create(name, new MetreOffset(x, y), gridIndex: routeOrder)
            with { ManualOverride = new PixelPoint(x, y), RouteOrder = routeOrder };
        var vm = new SurveyItemViewModel(s);
        session.Surveys.Add(vm);
        return vm;
    }

    // ─── Route start ─────────────────────────────────────────────────────

    [Fact]
    public void OptimizeRoute_starts_from_projected_player_pixel_when_calibrated()
    {
        var (session, map, _, _, opt) = BuildSut(calibrated: true, px: 40, pz: -25);
        var expected = Calib.ProjectWorld(new WorldCoord(40, 0, -25));
        SeedSurveyAt(session, "A", 200, 200, 0);
        SeedSurveyAt(session, "B", 300, 300, 1);

        map.OptimizeRouteCommand.Execute(null);

        session.SurveyPlayerPixel.Should().Be(expected);
        opt.LastStart.Should().Be(expected, "the tour must start from the player's GPS, not pin[0]");
    }

    [Fact]
    public void OptimizeRoute_falls_back_to_first_pin_when_uncalibrated()
    {
        var (session, map, _, _, opt) = BuildSut(calibrated: false);
        var first = SeedSurveyAt(session, "A", 210, 220, 0);
        SeedSurveyAt(session, "B", 300, 300, 1);

        map.OptimizeRouteCommand.Execute(null);

        session.SurveyPlayerPixel.Should().BeNull("no calibration ⇒ no projected anchor");
        opt.LastStart.Should().Be(first.EffectivePixel!.Value, "#454 fallback preserved");
    }

    // ─── Marker ──────────────────────────────────────────────────────────

    [Fact]
    public void Marker_resolves_only_when_a_player_pixel_exists()
    {
        var calibrated = BuildSut(calibrated: true, px: 12, pz: -8);
        calibrated.map.PlayerMarkerPixel.Should()
            .Be(Calib.ProjectWorld(new WorldCoord(12, 0, -8)));

        var uncalibrated = BuildSut(calibrated: false);
        uncalibrated.map.PlayerMarkerPixel.Should()
            .BeNull("no regression vs pre-#476: uncalibrated ⇒ no marker");
    }

    [Fact]
    public void Marker_is_motherlode_anchor_in_motherlode_mode()
    {
        var (session, map, _, _, _) = BuildSut(calibrated: true);
        session.Mode = SessionMode.Motherlode;

        map.PlayerMarkerPixel.Should().BeNull("Motherlode marker needs a recorded click");

        session.PlayerPosition = new PixelPoint(77, 88);
        session.HasPlayerPosition = true;
        map.PlayerMarkerPixel.Should().Be(new PixelPoint(77, 88));
    }

    [Fact]
    public void Live_fix_updates_the_anchor_on_zone_or_teleport()
    {
        var (session, _, tracker, _, _) = BuildSut(calibrated: true, px: 5, pz: -5);
        session.SurveyPlayerPixel.Should().Be(Calib.ProjectWorld(new WorldCoord(5, 0, -5)));

        tracker.Push(60, 0, -70, PlayerPositionSource.Movement);

        session.SurveyPlayerPixel.Should().Be(Calib.ProjectWorld(new WorldCoord(60, 0, -70)));
        session.SurveyPlayerSource.Should().Be(PlayerPositionSource.Movement);
    }

    [Fact]
    public void Anchor_resolves_when_calibration_applied_after_construction()
    {
        // Mirrors the live order: the overlay VM is built, then the area's
        // calibration is applied on zone-in (AreaCalibrationService.Changed).
        var (session, _, _, areaCal, _) = BuildSut(calibrated: false, px: 9, pz: -3);
        session.SurveyPlayerPixel.Should().BeNull();

        areaCal.SetCalibration(Calib);

        session.SurveyPlayerPixel.Should().Be(Calib.ProjectWorld(new WorldCoord(9, 0, -3)));
    }

    // ─── Initial guidance segment ────────────────────────────────────────

    [Fact]
    public void Initial_segment_runs_from_player_pixel_before_first_collection()
    {
        var (session, map, _, _, _) = BuildSut(calibrated: true, px: 10, pz: -10);
        var start = Calib.ProjectWorld(new WorldCoord(10, 0, -10));
        SeedSurveyAt(session, "First", 200, 200, 0);
        SeedSurveyAt(session, "Second", 300, 300, 1);

        map.ActiveSegmentPoints.Count.Should().Be(2);
        map.ActiveSegmentPoints[0].Should().Be(start);
        map.ActiveSegmentPoints[1].Should().Be(new PixelPoint(200, 200));
    }

    [Fact]
    public void Initial_segment_absent_before_first_collection_when_uncalibrated()
    {
        // #454 parity: no anchor, nothing collected ⇒ no segment.
        var (session, map, _, _, _) = BuildSut(calibrated: false);
        SeedSurveyAt(session, "First", 200, 200, 0);
        SeedSurveyAt(session, "Second", 300, 300, 1);

        map.ActiveSegmentPoints.Count.Should().Be(0);
    }

    // ─── Staleness surface ───────────────────────────────────────────────

    [Theory]
    [InlineData(PlayerPositionSource.Spawn, 0, "You — zone-in, just now")]
    [InlineData(PlayerPositionSource.Spawn, 240, "You — zone-in, 4m ago")]
    [InlineData(PlayerPositionSource.Movement, 7200, "You — teleport, 2h ago")]
    public void FormatAnchorStatus_reflects_source_and_age(
        PlayerPositionSource source, int ageSeconds, string expected)
    {
        var now = DateTimeOffset.UnixEpoch + TimeSpan.FromHours(10);
        var measuredAt = now - TimeSpan.FromSeconds(ageSeconds);

        MapOverlayViewModel.FormatAnchorStatus(measuredAt, source, now).Should().Be(expected);
    }

    [Fact]
    public void PlayerAnchorStatus_is_empty_until_an_anchor_resolves_and_only_in_survey_mode()
    {
        var (session, map, _, _, _) = BuildSut(calibrated: false);
        map.PlayerAnchorStatus.Should().BeEmpty("no anchor yet");
        map.IsPlayerAnchorStatusVisible.Should().BeFalse();

        var (s2, m2, _, _, _) = BuildSut(calibrated: true);
        m2.PlayerAnchorStatus.Should().NotBeEmpty("anchor resolved in Survey mode");
        m2.IsPlayerAnchorStatusVisible.Should().BeTrue();

        s2.Mode = SessionMode.Motherlode;
        m2.PlayerAnchorStatus.Should().BeEmpty("staleness label is Survey-only");
    }

    // ─── #476 Option C: manual override ──────────────────────────────────

    [Fact]
    public void Map_click_records_a_manual_override_only_while_setting_position()
    {
        var (session, map, _, _, _) = BuildSut(calibrated: true, px: 1, pz: -1);
        var auto = Calib.ProjectWorld(new WorldCoord(1, 0, -1));
        session.SurveyPlayerPixel.Should().Be(auto);

        // A click outside the detour does nothing in Survey mode.
        map.HandleMapClick(new PixelPoint(500, 600));
        session.SurveyPlayerPixel.Should().Be(auto);
        session.SurveyPlayerIsManual.Should().BeFalse();

        map.SetPositionCommand.Execute(null);
        map.IsSettingPosition.Should().BeTrue();
        map.OverlayHint.Should().NotBeEmpty("the detour coaches the click");

        map.HandleMapClick(new PixelPoint(500, 600));

        session.SurveyPlayerPixel.Should().Be(new PixelPoint(500, 600));
        session.SurveyPlayerIsManual.Should().BeTrue();
        session.SurveyPlayerSource.Should().BeNull("a manual pixel has no log source");
        map.PlayerMarkerPixel.Should().Be(new PixelPoint(500, 600));
        map.PlayerAnchorStatus.Should().Be("You — set manually");
        map.IsSettingPosition.Should().BeFalse("the click confirms and returns");
    }

    [Fact]
    public void Manual_override_survives_a_calibration_reapply_but_a_fresh_fix_supersedes_it()
    {
        var (session, map, tracker, areaCal, _) = BuildSut(calibrated: true, px: 2, pz: -2);

        map.SetPositionCommand.Execute(null);
        map.HandleMapClick(new PixelPoint(123, 456));
        session.SurveyPlayerIsManual.Should().BeTrue();

        // Calibration re-applied on the same area (e.g. recalibrate): the
        // manual pixel is calibration-independent, so it stays.
        areaCal.SetCalibration(Calib);
        session.SurveyPlayerPixel.Should().Be(new PixelPoint(123, 456));
        session.SurveyPlayerIsManual.Should().BeTrue();

        // A fresh tracker fix (zone-in / teleport) is authoritative again.
        tracker.Push(80, 0, -90, PlayerPositionSource.Movement);
        session.SurveyPlayerIsManual.Should().BeFalse();
        session.SurveyPlayerPixel.Should().Be(Calib.ProjectWorld(new WorldCoord(80, 0, -90)));
    }

    [Fact]
    public void Manual_override_works_even_when_area_is_uncalibrated()
    {
        // The whole point of Option C: an uncalibrated area has no auto
        // anchor, but the user can still place one by hand.
        var (session, map, _, _, opt) = BuildSut(calibrated: false);
        session.SurveyPlayerPixel.Should().BeNull();

        map.SetPositionCommand.Execute(null);
        map.HandleMapClick(new PixelPoint(42, 99));

        session.SurveyPlayerPixel.Should().Be(new PixelPoint(42, 99));
        session.SurveyPlayerIsManual.Should().BeTrue();

        SeedSurveyAt(session, "A", 300, 300, 0);
        map.OptimizeRouteCommand.Execute(null);
        opt.LastStart.Should().Be(new PixelPoint(42, 99),
            "the manual anchor seeds the route start even with no calibration");
    }

    [Fact]
    public void Cancel_leaves_the_anchor_unchanged()
    {
        var (session, map, _, _, _) = BuildSut(calibrated: true, px: 3, pz: -4);
        var auto = Calib.ProjectWorld(new WorldCoord(3, 0, -4));

        map.SetPositionCommand.Execute(null);
        map.IsSettingPosition.Should().BeTrue();
        map.CancelSetPositionCommand.Execute(null);

        map.IsSettingPosition.Should().BeFalse();
        session.SurveyPlayerPixel.Should().Be(auto, "cancel makes no change");
        session.SurveyPlayerIsManual.Should().BeFalse();
    }
}
