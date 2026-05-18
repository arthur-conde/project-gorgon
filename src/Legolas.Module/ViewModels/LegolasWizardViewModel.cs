using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.Sharing;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Dialogs;

namespace Legolas.ViewModels;

/// <summary>
/// Step the wizard is currently rendering. Combines a synthetic <see cref="PickMode"/>
/// gate (step 0) with the active <see cref="SurveyFlowState"/>, and four
/// derived Motherlode sub-steps (#113 Layer 4). The Motherlode steps are not
/// FSM states — <see cref="Flow.MotherlodeFlowController"/> stays coarse; they
/// are projected from <see cref="MotherlodeViewModel.Stage"/>, itself derived
/// from the log-driven coordinator snapshot.
/// </summary>
public enum WizardStep
{
    PickMode,
    Calibrating,
    Listening,
    Gathering,
    Done,
    /// <summary>Motherlode: no readings yet — prompt to click maps at ≥3 spots.</summary>
    MotherlodeMeasuring,
    /// <summary>Motherlode: readings in, nothing solved yet — keep going / spread out.</summary>
    MotherlodeLocating,
    /// <summary>Motherlode: ≥1 treasure located — the relative-guidance route card.</summary>
    MotherlodeWalk,
    /// <summary>Motherlode: every located treasure collected.</summary>
    MotherlodeDone,
}

/// <summary>
/// View-model for the Survey/Motherlode wizard. Owns the synthetic mode-pick
/// gate, then projects active flow controllers' state onto a single
/// <see cref="CurrentStep"/> property the view templates against.
/// </summary>
public sealed partial class LegolasWizardViewModel : ObservableObject
{
    private readonly SessionState _session;
    private readonly SurveyFlowController _surveyFlow;
    private readonly MotherlodeFlowController _motherlodeFlow;
    private readonly IAreaCalibrationService _areaCalibration;
    private readonly LegolasSettings _settings;
    private readonly LegolasReportService? _reportService;
    private readonly LegolasShareCardRenderer? _renderer;
    private readonly IActiveCharacterService? _activeChar;
    private readonly IReferenceDataService? _refData;
    private readonly IDialogService? _dialogs;

    public LegolasWizardViewModel(
        SessionState session,
        SurveyFlowController surveyFlow,
        MotherlodeFlowController motherlodeFlow,
        ControlPanelViewModel controlPanel,
        MotherlodeViewModel motherlode,
        MapOverlayViewModel mapOverlay,
        NudgePadViewModel nudgePad,
        IAreaCalibrationService areaCalibration,
        PinCalibrationCoordinator pinCalibration,
        LegolasSettings settings,
        LegolasReportService? reportService = null,
        LegolasShareCardRenderer? renderer = null,
        IActiveCharacterService? activeChar = null,
        IReferenceDataService? refData = null,
        IDialogService? dialogs = null)
    {
        _session = session;
        _surveyFlow = surveyFlow;
        _motherlodeFlow = motherlodeFlow;
        _settings = settings;
        _reportService = reportService;
        _renderer = renderer;
        _activeChar = activeChar;
        _refData = refData;
        _dialogs = dialogs;
        _areaCalibration = areaCalibration;
        PinCalibration = pinCalibration;
        ControlPanel = controlPanel;
        Motherlode = motherlode;
        MapOverlay = mapOverlay;
        NudgePad = nudgePad;

        _surveyFlow.PropertyChanged += OnSurveyFlowChanged;
        _surveyFlow.Transitioned += OnSurveyFlowTransitioned;
        _session.Surveys.CollectionChanged += OnSurveysChangedForOverlays;
        _motherlodeFlow.PropertyChanged += OnMotherlodeFlowChanged;
        Motherlode.PropertyChanged += OnMotherlodeViewModelChanged;
        _session.PropertyChanged += OnSessionChanged;
        // #460: once the area becomes calibrated (Confirm persisted it), leave
        // the Calibrating gate. #477B: a clear/(re)calibrate also flips
        // CanRecalibrate and must reset the confirm guard so a stale "are you
        // sure?" can't carry across areas.
        _areaCalibration.Changed += (_, _) =>
        {
            IsConfirmingRecalibrate = false;
            OnPropertyChanged(nameof(CanRecalibrate));
            OnPropertyChanged(nameof(IsAreaCalibrated));
            // #113 header chip: area and/or calibration state just changed.
            OnPropertyChanged(nameof(CurrentAreaName));
            OnPropertyChanged(nameof(IsAreaKnown));
            OnPropertyChanged(nameof(CalibrationChipText));
            OnPropertyChanged(nameof(CanCalibrateThisArea));
            // #113: once this area is calibrated the Motherlode dot can place;
            // drop the one-shot request so RecomputeStep returns to the
            // log-driven Motherlode stage instead of re-entering Calibrating.
            if (_areaCalibration.IsCurrentAreaCalibrated)
                _motherlodeCalibrationRequested = false;
            RecomputeStep();
        };
        if (_reportService is not null)
            _reportService.ReportGenerated += OnReportGenerated;
        RecomputeStep();
    }

    /// <summary>
    /// FSM-edge-driven overlay management (#454 collapsed FSM). A completed
    /// run is <c>… → Done</c>: hide both overlays so the game window is
    /// uncluttered between cycles. Re-showing happens on the next cycle's
    /// first pin (see <see cref="OnSurveysChangedForOverlays"/>) rather than
    /// on a state edge — the old Ready→Listening "next survey" edge is gone,
    /// and the auto-reset Done→Listening would otherwise re-show during the
    /// empty post-reset window. Gated on
    /// <see cref="LegolasSettings.HideOverlaysBetweenSessions"/>; a manual
    /// mid-session reset (which doesn't enter Done) doesn't hide — the test
    /// "Reset preserves overlay visibility" pins that.
    /// </summary>
    private void OnSurveyFlowTransitioned(SurveyTransition t)
    {
        if (!_settings.HideOverlaysBetweenSessions) return;

        if (t.To == SurveyFlowState.Done)
        {
            _session.IsMapVisible = false;
            _session.IsInventoryVisible = false;
        }
    }

    /// <summary>
    /// Re-show both overlays when the next cycle's first pin lands (count
    /// 0→1) — the collapsed-FSM replacement for the old Ready→Listening
    /// "next survey" re-show edge. Gated on the same opt-out so users who
    /// keep overlays always-visible are unaffected.
    /// </summary>
    private void OnSurveysChangedForOverlays(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!_settings.HideOverlaysBetweenSessions) return;
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
            && _session.Surveys.Count == 1)
        {
            _session.IsMapVisible = true;
            _session.IsInventoryVisible = true;
        }
    }

    /// <summary>
    /// True when at least one overlay is currently visible. Drives the wizard
    /// hero row's overlay-toggle button: a click flips both to the opposite
    /// state, mirroring the <c>ToggleAllOverlaysCommand</c> hotkey shape.
    /// </summary>
    public bool AreOverlaysVisible => _session.IsMapVisible || _session.IsInventoryVisible;

    /// <summary>
    /// Wizard-hero overlay toggle. Sets both <see cref="SessionState.IsMapVisible"/>
    /// and <see cref="SessionState.IsInventoryVisible"/> to the same target value
    /// (the opposite of <see cref="AreOverlaysVisible"/>) so the two overlays move
    /// in lockstep, matching the hotkey-driven <c>ToggleAllOverlaysCommand</c>.
    /// </summary>
    [RelayCommand]
    private void ToggleOverlays()
    {
        var target = !AreOverlaysVisible;
        _session.IsMapVisible = target;
        _session.IsInventoryVisible = target;
    }

    /// <summary>
    /// Opens/closes the standalone map-calibration overlay (same flag the
    /// unbound <c>ToggleCalibrationOverlayCommand</c> hotkey flips). This is the
    /// discoverable entry point — the feature is otherwise reachable only via a
    /// user-assigned hotkey.
    /// </summary>
    [RelayCommand]
    private void ToggleCalibration() =>
        _session.IsCalibrationVisible = !_session.IsCalibrationVisible;

    /// <summary>True once the user has clicked Survey or Motherlode in step 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStep))]
    [NotifyPropertyChangedFor(nameof(CanCalibrateThisArea))]
    private bool _hasPickedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStepTitle))]
    private WizardStep _currentStep = WizardStep.PickMode;

    partial void OnCurrentStepChanged(WizardStep value)
    {
        // #460: the Calibrating gate arms pin-capture on the map overlay
        // (which it opens); any other step disarms (flushes pending/pairs).
        if (value == WizardStep.Calibrating)
        {
            PinCalibration.Arm();
            _session.IsMapVisible = true;
        }
        else if (PinCalibration.IsArmed)
        {
            PinCalibration.Disarm();
        }

        // #454: no AwaitingPosition step. Entering Listening auto-opens the
        // map (so absolute pins are visible by default) and the inventory
        // (the user is picking which survey to use). Gathering keeps the
        // inventory open as a walk-the-route checklist.
        if (value is WizardStep.Listening or WizardStep.Gathering)
        {
            _session.IsInventoryVisible = true;
            if (value == WizardStep.Listening)
                _session.IsMapVisible = true;
        }

        // #113 Layer 5: once a treasure is located, the map overlay carries
        // the calibration-gated marker — open it so the dot is visible.
        if (value == WizardStep.MotherlodeWalk)
            _session.IsMapVisible = true;
    }

    /// <summary>Headline displayed inline with the wizard's per-step nav row.</summary>
    public string CurrentStepTitle => CurrentStep switch
    {
        WizardStep.PickMode => "What are you doing?",
        WizardStep.Calibrating => "Calibrate this area",
        WizardStep.Listening => "Use a survey",
        WizardStep.Gathering => "Walk your route",
        WizardStep.Done => "All collected",
        WizardStep.MotherlodeMeasuring => "Measure the treasure",
        WizardStep.MotherlodeLocating => "Locating…",
        WizardStep.MotherlodeWalk => "Walk to the treasure",
        WizardStep.MotherlodeDone => "All collected",
        _ => "",
    };

    public ControlPanelViewModel ControlPanel { get; }
    public MotherlodeViewModel Motherlode { get; }
    public MapOverlayViewModel MapOverlay { get; }
    public NudgePadViewModel NudgePad { get; }

    /// <summary>#460 cold-start pin-calibration driver — the Calibrating step
    /// binds its status/Solve to this.</summary>
    public PinCalibrationCoordinator PinCalibration { get; }

    public SessionState Session => _session;
    public SurveyFlowController SurveyFlow => _surveyFlow;
    public MotherlodeFlowController MotherlodeFlow => _motherlodeFlow;

    /// <summary>
    /// Mode-aware reset dispatched from the header's Reset icon. Wizard-level
    /// reset is "start this flow from scratch" — clears the player anchor,
    /// surveys, and any pending pin so the user lands back at step 2 (set
    /// position) for Survey or step 1 (record positions) for Motherlode.
    /// Overlays are preserved (per "Reset = do this flow again" rule).
    /// </summary>
    [RelayCommand]
    private void WizardReset()
    {
        if (_session.Mode == SessionMode.Motherlode)
        {
            Motherlode.ResetCommand.Execute(null);
            return;
        }
        // Survey (#454): no anchor — Reset clears surveys and lands on
        // Listening (the FSM's only resting state).
        _surveyFlow.Reset();
    }

    /// <summary>#477A: flip the guided walkthrough between the Drop and Pair
    /// phases. Lives on the wizard panel (a normal, always-clickable window) —
    /// the transparent overlay can't host the trigger while click-through.</summary>
    [RelayCommand]
    private void ToggleCalibrationPhase() => PinCalibration.TogglePhase();

    /// <summary>#477A: defer the currently-named pin and get the next
    /// spread suggestion (no pair recorded).</summary>
    [RelayCommand]
    private void SkipCalibrationPin() => PinCalibration.SkipSuggestion();

    /// <summary>#477A terminal Confirm: solve + persist, gated on ≥3 pairs and
    /// a good residual. On success the area is calibrated, <c>Changed</c>
    /// fires, and the wizard advances out of Calibrating (RecomputeStep).</summary>
    [RelayCommand]
    private void ConfirmCalibration()
    {
        PinCalibration.Confirm();
        RecomputeStep();
    }

    /// <summary>#477A "finish anyway": persist despite a high residual (still
    /// ≥3 pairs) — the non-affine ±10% ceiling means the user is never
    /// trapped.</summary>
    [RelayCommand]
    private void ConfirmCalibrationAnyway()
    {
        PinCalibration.ConfirmAnyway();
        RecomputeStep();
    }

    /// <summary>#477A: discard placed pairs and re-arm for a fresh attempt.</summary>
    [RelayCommand]
    private void ClearCalibrationPins() => PinCalibration.Arm();

    /// <summary>#477B: true once the user has clicked "Recalibrate this area"
    /// and we are waiting on the confirm guard (a misclick would wipe a good,
    /// persisted calibration).</summary>
    [ObservableProperty]
    private bool _isConfirmingRecalibrate;

    /// <summary>#477B: in-flow recalibration entry — only meaningful when the
    /// current area is already calibrated. Arms the confirm guard rather than
    /// destroying immediately.</summary>
    [RelayCommand]
    private void Recalibrate() => IsConfirmingRecalibrate = true;

    /// <summary>#477B: confirmed recalibrate. Clears the current area's
    /// persisted calibration; <see cref="IAreaCalibrationService.Changed"/>
    /// makes <see cref="RecomputeStep"/> route back into
    /// <see cref="WizardStep.Calibrating"/> via the same pin route as cold
    /// start (<see cref="OnCurrentStepChanged"/> re-arms PinCalibration), so
    /// Part A's guided correctable flow applies on the redo.</summary>
    [RelayCommand]
    private void ConfirmRecalibrate()
    {
        IsConfirmingRecalibrate = false;
        _areaCalibration.ClearCurrentAreaCalibration();
    }

    /// <summary>#477B: back out of the recalibrate confirm guard.</summary>
    [RelayCommand]
    private void CancelRecalibrate() => IsConfirmingRecalibrate = false;

    /// <summary>#477B: a "Recalibrate this area" affordance is offered only
    /// when there is a persisted calibration to redo (Listening step).</summary>
    public bool CanRecalibrate => _areaCalibration.IsCurrentAreaCalibrated;

    /// <summary>#113 Layer 5: true once the current area has an applied
    /// calibration — the only gate on the Motherlode on-map dot (the relative
    /// text is calibration-free). Drives the Walk panel's calibrate affordance
    /// vs. the honest "dot is approximate" caveat. Notified on
    /// <see cref="IAreaCalibrationService.Changed"/>.</summary>
    public bool IsAreaCalibrated => _areaCalibration.IsCurrentAreaCalibrated;

    /// <summary>#113: friendly name of the area Legolas thinks you're in, or
    /// null if none was detected (Mithril started mid-session with no
    /// "Entering Area" banner).</summary>
    public string? CurrentAreaName => _areaCalibration.CurrentAreaFriendlyName;

    /// <summary>True when the area is identified in reference data (so a
    /// calibration is even possible). False ⇒ the chip shows "area not
    /// detected" rather than a calibrate prompt.</summary>
    public bool IsAreaKnown => _areaCalibration.CurrentAreaKey is not null;

    /// <summary>#113: the always-visible header chip text — area + calibration
    /// state at a glance, so the user never has to open the (experimental)
    /// calibration overlay to find out.</summary>
    public string CalibrationChipText =>
        !IsAreaKnown ? "Area not detected"
        : IsAreaCalibrated ? $"{CurrentAreaName} · calibrated"
        : $"{CurrentAreaName} · not calibrated";

    /// <summary>The chip is an actionable "calibrate now" affordance only when
    /// a mode is picked, the area is known, and it isn't already calibrated;
    /// otherwise it's a passive status label.</summary>
    public bool CanCalibrateThisArea => HasPickedMode && IsAreaKnown && !IsAreaCalibrated;

    /// <summary>#113: start the guided Drop/Pair calibration from the header
    /// chip (never the experimental overlay). Survey already gates an
    /// uncalibrated area into <see cref="WizardStep.Calibrating"/>; Motherlode
    /// needs the explicit opt-in (it's calibration-free by default).</summary>
    [RelayCommand]
    private void CalibrateThisArea()
    {
        if (_session.Mode == SessionMode.Motherlode)
            _motherlodeCalibrationRequested = true;
        RecomputeStep();
    }

    /// <summary>#113: one-shot request to detour into the guided
    /// <see cref="WizardStep.Calibrating"/> walkthrough from Motherlode.
    /// Optional and non-blocking — measuring/locating/walking by relative text
    /// never needs it; it only unlocks the on-map dot. Cleared automatically
    /// once the area calibrates (or on mode change).</summary>
    private bool _motherlodeCalibrationRequested;

    /// <summary>#113: enter the same guided Drop/Pair calibration Survey uses,
    /// then fall back to the Motherlode stage once it persists. Reuses the
    /// existing <see cref="WizardStep.Calibrating"/> machinery via
    /// <see cref="RecomputeStep"/>.</summary>
    [RelayCommand]
    private void CalibrateForMotherlode()
    {
        _motherlodeCalibrationRequested = true;
        RecomputeStep();
    }

    /// <summary>
    /// Step-wise back. From the first post-pick step, returns to mode pick
    /// (delegates to <see cref="ChangeMode"/>). From mid-flow steps, undoes
    /// the most recent transition. From terminal/locked-in steps (Gathering,
    /// Done), full-resets to AwaitingPosition since the route's position
    /// anchor is invalidated once walking begins.
    /// </summary>
    [RelayCommand]
    private void Back()
    {
        switch (CurrentStep)
        {
            case WizardStep.Calibrating:
            case WizardStep.Listening:
            case WizardStep.MotherlodeMeasuring:
                // First post-pick step → back to mode pick.
                ChangeModeCommand.Execute(null);
                break;
            case WizardStep.Gathering:
            case WizardStep.Done:
                // Route in progress or done → reset the flow (clears surveys,
                // lands back on Listening).
                WizardResetCommand.Execute(null);
                break;
        }
    }

    [RelayCommand]
    private void PickSurveyMode()
    {
        _session.Mode = SessionMode.Survey;
        HasPickedMode = true;
        RecomputeStep();
    }

    [RelayCommand]
    private void PickMotherlodeMode()
    {
        _session.Mode = SessionMode.Motherlode;
        HasPickedMode = true;
        RecomputeStep();
    }

    /// <summary>
    /// Returns to step 0. Resets the active flow controller so its state
    /// doesn't leak into the next mode pick, and hides both overlays — the
    /// user is starting fresh, not continuing the same session. (Reset alone
    /// preserves overlays since the user is doing the same flow again.)
    /// </summary>
    [RelayCommand]
    private void ChangeMode()
    {
        if (_session.Mode == SessionMode.Survey)
            _surveyFlow.Reset();
        else
            _motherlodeFlow.Reset();
        _motherlodeCalibrationRequested = false;
        _session.IsMapVisible = false;
        _session.IsInventoryVisible = false;
        HasPickedMode = false;
        RecomputeStep();
    }

    private void OnSurveyFlowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SurveyFlowController.CurrentState))
            RecomputeStep();
    }

    private void OnMotherlodeFlowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MotherlodeFlowController.CurrentState))
            RecomputeStep();
    }

    // #113 Layer 4: the derived Motherlode stage moved (a reading landed, a
    // treasure solved, the last one was collected) — re-evaluate the step.
    private void OnMotherlodeViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MotherlodeViewModel.Stage))
            RecomputeStep();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Hotkey-driven mode flip should re-project the wizard's step.
        if (e.PropertyName == nameof(SessionState.Mode))
            RecomputeStep();
        // Bubble overlay-visibility changes up to AreOverlaysVisible so the
        // hero-row toggle button's icon/tooltip stay in sync with the actual
        // session state (toggled by the button itself, the hotkey, or the
        // FSM-edge auto-hide/show in OnSurveyFlowTransitioned).
        else if (e.PropertyName is nameof(SessionState.IsMapVisible)
                              or nameof(SessionState.IsInventoryVisible))
            OnPropertyChanged(nameof(AreOverlaysVisible));
    }

    /// <summary>
    /// True once a survey run has completed at least once this app session, so the
    /// wizard can show a "View last report" button. The snapshot lives on the
    /// report service across FSM resets, so this stays true even after AutoReset
    /// has cleared <see cref="SessionState"/>.
    /// </summary>
    public bool HasLatestReport => _reportService?.LatestReport is not null;

    private void OnReportGenerated(LegolasSharePayload payload)
    {
        OnPropertyChanged(nameof(HasLatestReport));
        if (_settings.ShowReportOnDone)
            ShowReportDialog(payload);
    }

    [RelayCommand]
    private void ViewLastReport()
    {
        var payload = _reportService?.LatestReport;
        if (payload is null) return;
        ShowReportDialog(payload);
    }

    private void ShowReportDialog(LegolasSharePayload payload)
    {
        if (_dialogs is null || _reportService is null) return;
        // Capture the just-built payload so the dialog can rebuild on character-name
        // toggle without the FSM having to be in Done at click time.
        var captured = payload;
        var hasName = !string.IsNullOrWhiteSpace(_activeChar?.ActiveCharacterName);
        var vm = new LegolasShareDialogViewModel(
            buildPayload: includeName =>
            {
                if (includeName == (captured.CharacterName != null)) return captured;
                // Toggle the character name on the captured snapshot rather than
                // re-snapshotting from a now-reset SessionState.
                return new LegolasSharePayload
                {
                    SchemaVersion = captured.SchemaVersion,
                    CharacterName = includeName ? _activeChar?.ActiveCharacterName : null,
                    StartedAt = captured.StartedAt,
                    CompletedAt = captured.CompletedAt,
                    Mode = captured.Mode,
                    SurveyCount = captured.SurveyCount,
                    CollectedItemsByInternalName = new Dictionary<string, int>(captured.CollectedItemsByInternalName, StringComparer.Ordinal),
                    UnknownByName = captured.UnknownByName is null
                        ? null
                        : new Dictionary<string, int>(captured.UnknownByName, StringComparer.Ordinal),
                };
            },
            renderer: _renderer,
            settings: _settings,
            hasCharacterName: hasName,
            refData: _refData);
        _dialogs.ShowDialog(vm, new LegolasShareDialog());
    }

    private void RecomputeStep()
    {
        if (!HasPickedMode)
        {
            CurrentStep = WizardStep.PickMode;
            return;
        }

        if (_session.Mode == SessionMode.Motherlode)
        {
            // #113 Layer 5: optional, non-blocking calibration detour. The
            // log-driven flow never needs calibration (relative text is
            // frame-internal); this fires only when the user asked for the
            // on-map dot in an uncalibrated area. Reuses the Survey guided
            // walkthrough; areaCalibration.Changed clears the request and
            // re-runs this, dropping back to the stage below.
            if (_motherlodeCalibrationRequested && !_areaCalibration.IsCurrentAreaCalibrated)
            {
                CurrentStep = WizardStep.Calibrating;
                return;
            }

            // #113 Layer 4: derived sub-steps from the log-driven coordinator
            // snapshot (via MotherlodeViewModel.Stage). The FSM stays coarse.
            CurrentStep = Motherlode.Stage switch
            {
                MotherlodeStage.Locating => WizardStep.MotherlodeLocating,
                MotherlodeStage.Walk => WizardStep.MotherlodeWalk,
                MotherlodeStage.Done => WizardStep.MotherlodeDone,
                _ => WizardStep.MotherlodeMeasuring,
            };
            return;
        }

        // #460: cold-start gate. An uncalibrated area places nothing
        // (placement is absolute) — route the user through Calibrating until
        // the area has a calibration; IAreaCalibrationService.Changed
        // re-runs this once Solve persists one. #454 collapsed the rest of
        // the Survey FSM (no AwaitingPosition/Ready); Listening is the
        // resting/default step and its UI adapts to an empty Surveys list.
        if (!_areaCalibration.IsCurrentAreaCalibrated)
        {
            CurrentStep = WizardStep.Calibrating;
            return;
        }

        CurrentStep = _surveyFlow.CurrentState switch
        {
            SurveyFlowState.Listening => WizardStep.Listening,
            SurveyFlowState.Gathering => WizardStep.Gathering,
            SurveyFlowState.Done => WizardStep.Done,
            // #476: the manual-override detour is transient — keep the panel
            // anchored to the step it was launched from (Listening vs
            // Gathering) rather than flashing a different step. The Set/Cancel
            // affordance toggles on MapOverlay.IsSettingPosition within those
            // same panels.
            SurveyFlowState.SettingPosition =>
                _surveyFlow.ReturnState == SurveyFlowState.Gathering
                    ? WizardStep.Gathering
                    : WizardStep.Listening,
            _ => WizardStep.Listening,
        };
    }
}
