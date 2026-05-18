using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Sharing;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Dialogs;

namespace Legolas.ViewModels;

/// <summary>
/// Step the wizard is currently rendering. Combines a synthetic <see cref="PickMode"/>
/// gate (step 0) with the active <see cref="SurveyFlowState"/>, and a single
/// <see cref="MotherlodeMeasuring"/> placeholder for the Motherlode flow (PR1
/// keeps it coarse — see Decision 4 in docs/agent-plans/legolas-wizard.md).
/// </summary>
public enum WizardStep
{
    PickMode,
    Listening,
    Gathering,
    Done,
    MotherlodeMeasuring,
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
        ControlPanel = controlPanel;
        Motherlode = motherlode;
        MapOverlay = mapOverlay;
        NudgePad = nudgePad;

        _surveyFlow.PropertyChanged += OnSurveyFlowChanged;
        _surveyFlow.Transitioned += OnSurveyFlowTransitioned;
        _session.Surveys.CollectionChanged += OnSurveysChangedForOverlays;
        _motherlodeFlow.PropertyChanged += OnMotherlodeFlowChanged;
        _session.PropertyChanged += OnSessionChanged;
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
    private bool _hasPickedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStepTitle))]
    private WizardStep _currentStep = WizardStep.PickMode;

    partial void OnCurrentStepChanged(WizardStep value)
    {
        // #454: no AwaitingPosition step. Entering Listening auto-opens the
        // map (so absolute pins are visible by default — replaces the map
        // auto-open the old anchor step provided) and the inventory (the user
        // is picking which survey to use). Gathering keeps the inventory open
        // as a walk-the-route checklist.
        if (value is WizardStep.Listening or WizardStep.Gathering)
        {
            _session.IsInventoryVisible = true;
            if (value == WizardStep.Listening)
                _session.IsMapVisible = true;
        }
    }

    /// <summary>Headline displayed inline with the wizard's per-step nav row.</summary>
    public string CurrentStepTitle => CurrentStep switch
    {
        WizardStep.PickMode => "What are you doing?",
        WizardStep.Listening => "Use a survey",
        WizardStep.Gathering => "Walk your route",
        WizardStep.Done => "All collected",
        WizardStep.MotherlodeMeasuring => "Motherlode",
        _ => "",
    };

    public ControlPanelViewModel ControlPanel { get; }
    public MotherlodeViewModel Motherlode { get; }
    public MapOverlayViewModel MapOverlay { get; }
    public NudgePadViewModel NudgePad { get; }

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

        // #454 collapsed the Survey FSM (no AwaitingPosition/Ready — absolute
        // placement needs no anchor). Listening is the resting/default step;
        // its UI already adapts to an empty Surveys list. (The cold-start
        // Calibrating gate is added in the follow-up PR — see #460.)
        CurrentStep = _session.Mode == SessionMode.Motherlode
            ? WizardStep.MotherlodeMeasuring
            : _surveyFlow.CurrentState switch
            {
                SurveyFlowState.Listening => WizardStep.Listening,
                SurveyFlowState.Gathering => WizardStep.Gathering,
                SurveyFlowState.Done => WizardStep.Done,
                _ => WizardStep.Listening,
            };
    }
}
