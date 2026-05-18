using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Tests;
using Legolas.ViewModels;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #454 collapsed the Survey wizard: there is no <c>AwaitingPosition</c> step
/// (placement is absolute, no anchor). PickMode → Listening directly;
/// Listening auto-opens map + inventory. The cold-start <c>Calibrating</c>
/// step is added in the follow-up PR (#460) and is not exercised here.
/// </summary>
public class LegolasWizardViewModelTests
{
    // #460: a minimal IAreaCalibrationService. Defaults to *calibrated* so
    // the existing flow tests bypass the Calibrating gate; gate tests pass
    // one with Calibrated=false and flip it + RaiseChanged().
    private sealed class FakeAreaCalib : IAreaCalibrationService
    {
        public bool Calibrated { get; set; } = true;
        public bool IsCurrentAreaCalibrated => Calibrated;
        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public string? CurrentAreaKey => "AreaTest";
        public string? CurrentAreaFriendlyName => "Test";
        public AreaCalibration? CurrentCalibration => null;
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Array.Empty<CalibrationReference>();
        public IReadOnlyList<Mithril.Shared.Reference.AreaEntry> AllAreas => Array.Empty<Mithril.Shared.Reference.AreaEntry>();
        public event EventHandler? Changed;
        public void OnAreaEntered(string areaFriendlyName) { }
        public void SelectArea(string areaKey) { }
        public AreaCalibration? CalibrateCurrentArea(
            IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements, double calibrationZoom = 1.0)
        {
            Calibrated = true;
            var c = new AreaCalibration(1, 0, 0, 0, placements.Count, 0);
            Changed?.Invoke(this, EventArgs.Empty);
            return c;
        }
        public void ClearCurrentAreaCalibration() { }
        public void NoteSurvey(string name, MetreOffset offset) { }
        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved { add { } remove { } }
    }

    private static (LegolasWizardViewModel wizard, SessionState session, SurveyFlowController surveyFlow,
        MotherlodeFlowController motherlodeFlow, LegolasSettings settings) BuildSut(
        FakeAreaCalib? calib = null, FakePlayerPinTracker? pins = null)
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var motherlodeFlow = new MotherlodeFlowController(session);
        var controlPanel = new ControlPanelViewModel(settings, session, surveyFlow);
        var trilat = new TrilaterationSolver();
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var areaCalib = calib ?? new FakeAreaCalib();
        var pinCal = new PinCalibrationCoordinator(areaCalib, pins ?? new FakePlayerPinTracker());
        var motherlode = new MotherlodeViewModel(trilat, optimizer, session, motherlodeFlow);
        var mapOverlay = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes, settings, pinCal);
        var nudgePad = new NudgePadViewModel(session, mapOverlay, settings);
        var wizard = new LegolasWizardViewModel(session, surveyFlow, motherlodeFlow,
            controlPanel, motherlode, mapOverlay, nudgePad, areaCalib, pinCal, settings);
        return (wizard, session, surveyFlow, motherlodeFlow, settings);
    }

    private static SurveyItemViewModel Pin(string name = "Diamond", double px = 10, double py = 20) =>
        new(Survey.CreateAbsolute(name, new WorldCoord(px, 0, py), new PixelPoint(px, py), 0));

    [Fact]
    public void Initial_state_is_PickMode_with_HasPickedMode_false()
    {
        var (wizard, _, _, _, _) = BuildSut();
        wizard.HasPickedMode.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
    }

    [Fact]
    public void PickSurveyMode_advances_straight_to_Listening()
    {
        var (wizard, session, _, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.HasPickedMode.Should().BeTrue();
        session.Mode.Should().Be(SessionMode.Survey);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);
    }

    [Fact]
    public void PickMotherlodeMode_advances_to_MotherlodeMeasuring()
    {
        var (wizard, session, _, _, _) = BuildSut();
        wizard.PickMotherlodeModeCommand.Execute(null);
        wizard.HasPickedMode.Should().BeTrue();
        session.Mode.Should().Be(SessionMode.Motherlode);
        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);
    }

    [Fact]
    public void Entering_Listening_auto_opens_map_and_inventory()
    {
        var (wizard, session, _, _, _) = BuildSut();
        session.IsMapVisible.Should().BeFalse();
        session.IsInventoryVisible.Should().BeFalse();

        wizard.PickSurveyModeCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void Survey_happy_path_steps_through_each_transition()
    {
        var (wizard, session, surveyFlow, _, settings) = BuildSut();
        settings.AutoResetWhenAllCollected = false;
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        session.Surveys.Add(Pin("Diamond"));
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        surveyFlow.OptimizeRoute();
        wizard.CurrentStep.Should().Be(WizardStep.Gathering);

        var item = session.Surveys[0];
        item.UpdateModel(item.Model with { Collected = true });
        wizard.CurrentStep.Should().Be(WizardStep.Done);
    }

    [Fact]
    public void ChangeMode_returns_to_PickMode_resets_flow_and_hides_overlays()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
        session.Surveys.Add(Pin());

        wizard.ChangeModeCommand.Execute(null);

        wizard.HasPickedMode.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
        session.IsMapVisible.Should().BeFalse();
        session.IsInventoryVisible.Should().BeFalse();
        // #454: Reset always lands on Listening (no anchor precondition).
        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.Surveys.Should().BeEmpty();
    }

    [Fact]
    public void External_mode_flip_after_pick_re_projects_step()
    {
        var (wizard, session, _, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        session.Mode = SessionMode.Motherlode;

        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);
    }

    [Fact]
    public void Reset_preserves_overlay_visibility()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.IsMapVisible = true;
        session.IsInventoryVisible = true;

        // Reset from Listening (no Done edge) — "do this flow again".
        surveyFlow.Reset();

        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void Entering_Gathering_reopens_inventory_overlay()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.Surveys.Add(Pin());
        session.IsInventoryVisible = false; // user closed it mid-Listening

        surveyFlow.OptimizeRoute();

        wizard.CurrentStep.Should().Be(WizardStep.Gathering);
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void WizardReset_from_Listening_clears_surveys_and_stays_Listening()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.Surveys.Add(Pin());
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        wizard.WizardResetCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        session.Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Back_from_Listening_returns_to_PickMode()
    {
        var (wizard, _, _, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        wizard.BackCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
    }

    [Fact]
    public void Back_from_Gathering_resets_to_Listening()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.Surveys.Add(Pin());
        surveyFlow.OptimizeRoute();
        wizard.CurrentStep.Should().Be(WizardStep.Gathering);

        wizard.BackCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        session.Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Overlays_auto_hide_on_Done_and_re_show_on_next_survey()
    {
        var (_, session, surveyFlow, _, settings) = BuildSut();
        settings.AutoResetWhenAllCollected = true;
        settings.HideOverlaysBetweenSessions = true;

        // Cycle 1: first pin (re-show), collect → Done (auto-reset → Listening).
        var s1 = Pin("Diamond");
        session.Surveys.Add(s1);
        s1.UpdateModel(s1.Model with { Collected = true });

        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Listening,
            "auto-reset returned to Listening after Done");
        session.IsMapVisible.Should().BeFalse("entering Done hides the map");
        session.IsInventoryVisible.Should().BeFalse("entering Done hides the inventory");

        // Cycle 2: next pin (Surveys 0→1) re-shows both.
        session.Surveys.Add(Pin("Coal", 30, 40));

        session.IsMapVisible.Should().BeTrue("next survey re-shows the map");
        session.IsInventoryVisible.Should().BeTrue("next survey re-shows the inventory");
    }

    [Fact]
    public void Overlay_auto_hide_does_nothing_when_setting_off()
    {
        var (wizard, session, surveyFlow, _, settings) = BuildSut();
        settings.AutoResetWhenAllCollected = true;
        settings.HideOverlaysBetweenSessions = false;

        wizard.PickSurveyModeCommand.Execute(null); // map + inventory open
        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();

        var s1 = Pin("Diamond");
        session.Surveys.Add(s1);
        s1.UpdateModel(s1.Model with { Collected = true });

        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.IsMapVisible.Should().BeTrue("setting off → Done doesn't hide");
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void Overlay_auto_hide_does_not_fire_on_manual_reset_mid_session()
    {
        var (_, session, surveyFlow, _, settings) = BuildSut();
        settings.HideOverlaysBetweenSessions = true;
        session.Surveys.Add(Pin());
        session.IsMapVisible = true;
        session.IsInventoryVisible = true;

        surveyFlow.Reset(); // Listening→Listening (no Done edge)

        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void ToggleOverlays_when_either_visible_hides_both()
    {
        var (wizard, session, _, _, _) = BuildSut();
        session.IsMapVisible = true;
        session.IsInventoryVisible = false;
        wizard.AreOverlaysVisible.Should().BeTrue();

        wizard.ToggleOverlaysCommand.Execute(null);

        session.IsMapVisible.Should().BeFalse();
        session.IsInventoryVisible.Should().BeFalse();
        wizard.AreOverlaysVisible.Should().BeFalse();
    }

    [Fact]
    public void ToggleOverlays_when_both_hidden_shows_both()
    {
        var (wizard, session, _, _, _) = BuildSut();
        session.IsMapVisible = false;
        session.IsInventoryVisible = false;
        wizard.AreOverlaysVisible.Should().BeFalse();

        wizard.ToggleOverlaysCommand.Execute(null);

        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
        wizard.AreOverlaysVisible.Should().BeTrue();
    }

    [Fact]
    public void AreOverlaysVisible_change_notifies_when_session_visibility_flips()
    {
        var (wizard, session, _, _, _) = BuildSut();
        session.IsMapVisible = false;
        session.IsInventoryVisible = false;
        var notifications = 0;
        wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegolasWizardViewModel.AreOverlaysVisible))
                notifications++;
        };

        session.IsMapVisible = true;
        session.IsInventoryVisible = true;

        notifications.Should().BeGreaterOrEqualTo(2);
    }

    // ─── #460 cold-start Calibrating gate ────────────────────────────────

    [Fact]
    public void PickSurveyMode_on_uncalibrated_area_goes_to_Calibrating()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);

        wizard.PickSurveyModeCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);
        wizard.PinCalibration.IsArmed.Should().BeTrue("entering Calibrating arms map-overlay capture");
    }

    [Fact]
    public void Calibrating_advances_to_Listening_when_area_becomes_calibrated()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);

        calib.Calibrated = true;
        calib.RaiseChanged(); // IAreaCalibrationService.Changed → RecomputeStep

        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        wizard.PinCalibration.IsArmed.Should().BeFalse("leaving Calibrating disarms capture");
    }

    [Fact]
    public void SolveCalibration_persists_and_leaves_the_gate()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var pins = new FakePlayerPinTracker();
        var (wizard, _, _, _, _) = BuildSut(calib, pins);
        wizard.PickSurveyModeCommand.Execute(null);

        // Three fresh pin drops, then click-pair + Solve.
        pins.Add(1, 2);
        pins.Add(3, 4);
        pins.Add(5, 6);
        wizard.MapOverlay.PairCalibrationClick(new PixelPoint(10, 10));
        wizard.MapOverlay.PairCalibrationClick(new PixelPoint(20, 20));
        wizard.MapOverlay.PairCalibrationClick(new PixelPoint(30, 30));
        wizard.PinCalibration.CanSolve.Should().BeTrue();

        wizard.SolveCalibrationCommand.Execute(null);

        calib.Calibrated.Should().BeTrue();
        wizard.CurrentStep.Should().Be(WizardStep.Listening);
    }

    [Fact]
    public void Back_from_Calibrating_returns_to_PickMode()
    {
        var calib = new FakeAreaCalib { Calibrated = false };
        var (wizard, _, _, _, _) = BuildSut(calib);
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.Calibrating);

        wizard.BackCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
        wizard.PinCalibration.IsArmed.Should().BeFalse();
    }
}
