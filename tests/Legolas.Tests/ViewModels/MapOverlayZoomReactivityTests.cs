using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Xunit;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #524: dragging <see cref="SessionState.CurrentMapZoom"/> live-reprojects
/// every calibration-aware surface (player marker, Motherlode markers,
/// validate-calibration ghosts) and updates the zoom-mismatch warning chip
/// + legacy-recalibrate hint without a debounce / refresh button.
/// </summary>
public class MapOverlayZoomReactivityTests
{
    private static (MapOverlayViewModel map, FakeAreaCalibrationService cal, SessionState session)
        Build(AreaCalibration? seedCal = null)
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var cal = new FakeAreaCalibrationService();
        if (seedCal is not null) cal.SetCalibration(seedCal);
        var map = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes,
            settings, pinCalibration: null, positionState: null, bus: null, areaCalibration: cal);
        return (map, cal, session);
    }

    [Fact]
    public void CurrentMapZoom_change_fires_relevant_PropertyChanged_events()
    {
        // Seed a calibration so the warning chip + marker pixels participate.
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0) { CalibrationZoom = 2.0 };
        var (map, _, session) = Build(calibration);

        var fired = new HashSet<string>();
        map.PropertyChanged += (_, e) => { if (e.PropertyName is { } n) fired.Add(n); };

        session.CurrentMapZoom = 1.0;

        // The player marker / motherlode markers / validate ghosts collection /
        // warning chip all depend on the live zoom, so they must all refresh.
        fired.Should().Contain(nameof(MapOverlayViewModel.PlayerMarkerPixel));
        fired.Should().Contain(nameof(MapOverlayViewModel.MotherlodeMarkerPixels));
        fired.Should().Contain(nameof(MapOverlayViewModel.IsZoomMismatchWarningVisible));
    }

    [Fact]
    public void Warning_chip_appears_when_zoom_diverges_from_calibration_stamp()
    {
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0) { CalibrationZoom = 2.0 };
        var (map, _, session) = Build(calibration);

        // Stamped at 2.0; matching zoom — no warning.
        session.CurrentMapZoom = 2.0;
        map.IsZoomMismatchWarningVisible.Should().BeFalse();

        // Within the half-tick epsilon — still no warning.
        session.CurrentMapZoom = 1.96;
        map.IsZoomMismatchWarningVisible.Should().BeFalse();

        // Beyond the epsilon — warning fires.
        session.CurrentMapZoom = 1.5;
        map.IsZoomMismatchWarningVisible.Should().BeTrue();
        map.ZoomMismatchText.Should().Contain("2.00");
    }

    [Fact]
    public void Warning_chip_suppressed_on_legacy_one_point_zero_stamp()
    {
        // A pre-#524 calibration (CalibrationZoom = 1.0 default) must NOT
        // flag a warning when the user is at the default 2.0 — they had no
        // way to set the stamp deliberately. The legacy hint handles that.
        var legacy = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0); // CalibrationZoom defaults 1.0
        var (map, _, _) = Build(legacy);

        map.IsZoomMismatchWarningVisible.Should().BeFalse(
            "legacy stamps never trigger the warning — the recalibrate hint covers them");
        map.IsLegacyRecalibrateHintVisible.Should().BeTrue();
    }

    [Fact]
    public void Legacy_hint_dismissal_is_session_ephemeral_per_area()
    {
        var legacy = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (map, _, _) = Build(legacy);

        map.IsLegacyRecalibrateHintVisible.Should().BeTrue();
        map.DismissLegacyRecalibrateHintCommand.Execute(null);
        map.IsLegacyRecalibrateHintVisible.Should().BeFalse();
    }

    [Fact]
    public void Legacy_hint_disappears_when_recalibration_lifts_stamp_off_one()
    {
        var legacy = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0);
        var (map, cal, _) = Build(legacy);
        map.IsLegacyRecalibrateHintVisible.Should().BeTrue();

        // Recalibrate at 2.0 — stamp moves off 1.0.
        cal.SetCalibration(new AreaCalibration(2.0, 0.0, 100, 200, 3, 0) { CalibrationZoom = 2.0 });

        map.IsLegacyRecalibrateHintVisible.Should().BeFalse();
    }

    [Fact]
    public void Auto_seeds_CurrentMapZoom_from_calibration_stamp_on_area_change()
    {
        // Fresh session defaults to 2.0. Loading an area whose calibration
        // stamp is 0.5 should pull the slider to 0.5 — the user's "what
        // zoom should I dial PG to after restart?" answer comes from the
        // stamp, surfaced via auto-seed.
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0) { CalibrationZoom = 0.5 };
        var (map, cal, session) = Build();
        session.CurrentMapZoom.Should().Be(2.0, "default before any calibration is loaded");

        cal.SetCalibration(calibration);

        session.CurrentMapZoom.Should().Be(0.5);
        map.CalibrationZoomLabel.Should().Contain("0.50");
        map.IsCalibrationZoomLabelVisible.Should().BeTrue();
    }

    [Fact]
    public void Recalibrating_same_area_does_not_clobber_user_slider_value()
    {
        // After area-load auto-seed sets slider to 0.5, the user might have
        // manually moved it to 1.5 (e.g., they changed PG's zoom). A fresh
        // Changed event from a recalibration of the SAME area must not pull
        // the slider back — it's the user's value now.
        var calibration = new AreaCalibration(2.0, 0.0, 100, 200, 3, 0) { CalibrationZoom = 0.5 };
        var (_, cal, session) = Build();
        cal.SetCalibration(calibration);            // first load → seed to 0.5
        session.CurrentMapZoom.Should().Be(0.5);

        session.CurrentMapZoom = 1.5;               // user moved the slider

        // Recalibrate the same area (key unchanged) — Changed fires again.
        cal.SetCalibration(new AreaCalibration(2.0, 0.0, 100, 200, 3, 0) { CalibrationZoom = 0.5 });

        session.CurrentMapZoom.Should().Be(1.5, "same-area Changed must not clobber user edits");
    }

    [Fact]
    public void IsZoomFieldVisible_hides_when_overlay_click_through_is_on()
    {
        var (map, _, _) = Build();
        map.IsZoomFieldVisible.Should().BeTrue("default settings: click-through off");

        // We need a settings handle — grab via reflection-free path by
        // constructing with the same settings instance.
        var session = new SessionState();
        var settings = new LegolasSettings { ClickThroughMap = true };
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var cal = new FakeAreaCalibrationService();
        var map2 = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes,
            settings, pinCalibration: null, positionState: null, bus: null, areaCalibration: cal);

        map2.IsZoomFieldVisible.Should().BeFalse("ClickThroughMap=true hides the overlay strip");
    }
}
