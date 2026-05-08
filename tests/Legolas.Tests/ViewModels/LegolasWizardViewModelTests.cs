using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;

namespace Legolas.Tests.ViewModels;

public class LegolasWizardViewModelTests
{
    private static readonly DateTime FixedTime = new(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);

    private static (LegolasWizardViewModel wizard, SessionState session, SurveyFlowController surveyFlow,
        MotherlodeFlowController motherlodeFlow, LegolasSettings settings) BuildSut()
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
        var motherlode = new MotherlodeViewModel(trilat, optimizer, session, motherlodeFlow);
        var mapOverlay = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes, settings);
        var nudgePad = new NudgePadViewModel(session, mapOverlay, settings);
        var wizard = new LegolasWizardViewModel(session, surveyFlow, motherlodeFlow,
            controlPanel, motherlode, mapOverlay, nudgePad, settings);
        return (wizard, session, surveyFlow, motherlodeFlow, settings);
    }

    [Fact]
    public void Initial_state_is_PickMode_with_HasPickedMode_false()
    {
        var (wizard, _, _, _, _) = BuildSut();
        wizard.HasPickedMode.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
    }

    [Fact]
    public void PickSurveyMode_advances_to_AwaitingPosition()
    {
        var (wizard, session, _, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.HasPickedMode.Should().BeTrue();
        session.Mode.Should().Be(SessionMode.Survey);
        wizard.CurrentStep.Should().Be(WizardStep.AwaitingPosition);
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
    public void Survey_happy_path_re_projects_step_through_each_transition()
    {
        var (wizard, session, surveyFlow, _, settings) = BuildSut();
        // Disable auto-reset so we can observe the Done step before the controller bounces back.
        settings.AutoResetWhenAllCollected = false;
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.AwaitingPosition);

        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        // Surveys auto-place after the rework — NoteSurveyDetected stays in Listening
        // and surfaces the inventory overlay; no AwaitingPin step in between.
        var sd = new SurveyDetected(FixedTime, "Diamond", new MetreOffset(50, 30));
        surveyFlow.NoteSurveyDetected(sd);
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        // Add at least one pin so OptimizeRoute is meaningful (state machine permits the transition regardless).
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), gridIndex: 0)));
        surveyFlow.OptimizeRoute();
        wizard.CurrentStep.Should().Be(WizardStep.Gathering);

        // Mark all collected → Done via the AllCollected event on Surveys.
        var item = session.Surveys[0];
        item.UpdateModel(item.Model with { Collected = true });
        wizard.CurrentStep.Should().Be(WizardStep.Done);
    }

    [Fact]
    public void ChangeMode_returns_to_PickMode_resets_flow_and_hides_overlays()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        // Entering AwaitingPosition auto-opens the map; user opens inventory too.
        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible = true;
        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();
        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Ready);

        wizard.ChangeModeCommand.Execute(null);

        wizard.HasPickedMode.Should().BeFalse();
        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
        session.IsMapVisible.Should().BeFalse();
        session.IsInventoryVisible.Should().BeFalse();
        // FSM Reset preserves player position, so SurveyFlow returns to Ready
        // (per controller semantics — Reset doesn't clear the anchor).
        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Ready);
    }

    [Fact]
    public void External_mode_flip_after_pick_re_projects_step()
    {
        var (wizard, session, _, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.AwaitingPosition);

        // Simulate a hotkey-driven mode flip (SetMotherlodeModeCommand).
        session.Mode = SessionMode.Motherlode;

        wizard.CurrentStep.Should().Be(WizardStep.MotherlodeMeasuring);
    }

    [Fact]
    public void Reset_preserves_overlay_visibility()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();
        // User has both overlays open mid-session.
        session.IsMapVisible = true;
        session.IsInventoryVisible = true;

        // Reset (via the wizard's "Reset" button → ControlPanel.StartSession → SurveyFlow.Reset)
        // is "do this flow again" — overlays should stay open.
        surveyFlow.Reset();

        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void Entering_AwaitingPosition_auto_opens_map_overlay()
    {
        var (wizard, session, _, _, _) = BuildSut();
        session.IsMapVisible.Should().BeFalse();

        wizard.PickSurveyModeCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.AwaitingPosition);
        session.IsMapVisible.Should().BeTrue();
    }

    [Fact]
    public void Entering_Listening_auto_opens_inventory_overlay()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.IsInventoryVisible.Should().BeFalse();

        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();

        wizard.CurrentStep.Should().Be(WizardStep.Listening);
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void Entering_Gathering_reopens_inventory_overlay()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();
        // User manually closed the inventory mid-Listening.
        session.IsInventoryVisible = false;
        // Place a pin so OptimizeRoute is meaningful.
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), gridIndex: 0)));

        surveyFlow.OptimizeRoute();

        wizard.CurrentStep.Should().Be(WizardStep.Gathering);
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void WizardReset_from_Listening_clears_anchor_and_returns_to_AwaitingPosition()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        wizard.WizardResetCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.AwaitingPosition);
        session.HasPlayerPosition.Should().BeFalse();
    }

    [Fact]
    public void Back_from_AwaitingPosition_returns_to_PickMode()
    {
        var (wizard, _, _, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        wizard.CurrentStep.Should().Be(WizardStep.AwaitingPosition);

        wizard.BackCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.PickMode);
    }

    [Fact]
    public void Back_from_Listening_returns_to_AwaitingPosition_clearing_anchor()
    {
        var (wizard, session, surveyFlow, _, _) = BuildSut();
        wizard.PickSurveyModeCommand.Execute(null);
        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();
        wizard.CurrentStep.Should().Be(WizardStep.Listening);

        wizard.BackCommand.Execute(null);

        wizard.CurrentStep.Should().Be(WizardStep.AwaitingPosition);
        session.HasPlayerPosition.Should().BeFalse();
    }

    /// <summary>
    /// End-to-end happy path covering the auto-hide / auto-show round trip.
    /// Run #1: anchor → first pin → collect → auto-reset (hide). Run #2:
    /// next pin → re-show. Pins both edges in one fixture so the contract
    /// is described as a single observable cycle, not two disconnected facts.
    /// </summary>
    [Fact]
    public void Overlays_auto_hide_on_DoneToReady_and_re_show_on_next_survey()
    {
        var (_, session, surveyFlow, _, settings) = BuildSut();
        settings.AutoResetWhenAllCollected = true;
        settings.HideOverlaysBetweenSessions = true;

        // Cycle 1: anchor + first survey + collect → auto-reset fires Done→Ready.
        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);  // Ready→Listening — overlays already true here
        s1.UpdateModel(s1.Model with { Collected = true });

        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Ready,
            "auto-reset returned to Ready after Done");
        session.IsMapVisible.Should().BeFalse("Done→Ready hides the map");
        session.IsInventoryVisible.Should().BeFalse("Done→Ready hides the inventory");

        // Cycle 2: next survey arrives in Ready → both overlays re-show.
        var s2 = new SurveyItemViewModel(Survey.Create("Coal", new MetreOffset(10, 0), 0));
        session.Surveys.Add(s2);

        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Listening,
            "first pin in cycle 2 takes Ready→Listening");
        session.IsMapVisible.Should().BeTrue("Ready→Listening re-shows the map");
        session.IsInventoryVisible.Should().BeTrue("Ready→Listening re-shows the inventory");
    }

    [Fact]
    public void Overlay_auto_hide_does_nothing_when_setting_off()
    {
        var (wizard, session, surveyFlow, _, settings) = BuildSut();
        settings.AutoResetWhenAllCollected = true;
        settings.HideOverlaysBetweenSessions = false;

        // Drive through the full happy path so OnCurrentStepChanged opens
        // both overlays — the auto-hide-disabled assertion is only meaningful
        // when there's something to (not) hide.
        wizard.PickSurveyModeCommand.Execute(null);  // map opens (AwaitingPosition)
        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();          // inventory opens (WizardStep.Listening)
        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();

        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        s1.UpdateModel(s1.Model with { Collected = true });

        surveyFlow.CurrentState.Should().Be(SurveyFlowState.Ready);
        // With the setting off, the FSM-edge handler doesn't touch overlay
        // visibility — they stay where the OnCurrentStepChanged path left them.
        session.IsMapVisible.Should().BeTrue();
        session.IsInventoryVisible.Should().BeTrue();
    }

    [Fact]
    public void Overlay_auto_hide_does_not_fire_on_manual_reset_mid_session()
    {
        // Listening→Ready (manual Reset call from a live mid-session state)
        // is NOT the Done→Ready edge — the user intent is "do this flow
        // again", not "session ended". Overlays must stay where they are.
        // Belt-and-braces alongside Reset_preserves_overlay_visibility.
        var (_, session, surveyFlow, _, settings) = BuildSut();
        settings.HideOverlaysBetweenSessions = true;
        session.HasPlayerPosition = true;
        surveyFlow.ConfirmPlayerPosition();
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        session.IsMapVisible = true;
        session.IsInventoryVisible = true;

        surveyFlow.Reset();  // Listening → Ready (not Done→Ready)

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

        session.IsMapVisible = true;  // false → true should notify
        session.IsInventoryVisible = true;  // false → true should notify

        notifications.Should().BeGreaterOrEqualTo(2);
    }
}
