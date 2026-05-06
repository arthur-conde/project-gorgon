using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Legolas.Domain;
using Legolas.Flow;

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
    AwaitingPosition,
    Listening,
    AwaitingPin,
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

    public LegolasWizardViewModel(
        SessionState session,
        SurveyFlowController surveyFlow,
        MotherlodeFlowController motherlodeFlow,
        ControlPanelViewModel controlPanel,
        MotherlodeViewModel motherlode,
        MapOverlayViewModel mapOverlay)
    {
        _session = session;
        _surveyFlow = surveyFlow;
        _motherlodeFlow = motherlodeFlow;
        ControlPanel = controlPanel;
        Motherlode = motherlode;
        MapOverlay = mapOverlay;

        _surveyFlow.PropertyChanged += OnSurveyFlowChanged;
        _motherlodeFlow.PropertyChanged += OnMotherlodeFlowChanged;
        _session.PropertyChanged += OnSessionChanged;
        RecomputeStep();
    }

    /// <summary>True once the user has clicked Survey or Motherlode in step 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStep))]
    private bool _hasPickedMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentStepTitle))]
    private WizardStep _currentStep = WizardStep.PickMode;

    partial void OnCurrentStepChanged(WizardStep value)
    {
        // Entering AwaitingPosition (the "click on the map to set player position"
        // step) auto-opens the map overlay so the user has something to click.
        if (value == WizardStep.AwaitingPosition)
            _session.IsMapVisible = true;
    }

    /// <summary>Headline displayed inline with the wizard's per-step nav row.</summary>
    public string CurrentStepTitle => CurrentStep switch
    {
        WizardStep.PickMode => "What are you doing?",
        WizardStep.AwaitingPosition => "Show me where you are",
        WizardStep.Listening => "Use a survey",
        WizardStep.AwaitingPin => "Place this pin",
        WizardStep.Gathering => "Walk your route",
        WizardStep.Done => "All collected",
        WizardStep.MotherlodeMeasuring => "Motherlode",
        _ => "",
    };

    public ControlPanelViewModel ControlPanel { get; }
    public MotherlodeViewModel Motherlode { get; }
    public MapOverlayViewModel MapOverlay { get; }

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
        // Survey: nuke the anchor too so the FSM lands on AwaitingPosition,
        // not Listening — gives a visible "back to step 1" effect even when
        // there are no pins to clear.
        _session.HasPlayerPosition = false;
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
            case WizardStep.AwaitingPosition:
            case WizardStep.MotherlodeMeasuring:
                ChangeModeCommand.Execute(null);
                break;
            case WizardStep.Listening:
                // Re-anchor: drop the player position so we land on AwaitingPosition,
                // and clear surveys (they were anchored to the old position).
                _session.HasPlayerPosition = false;
                _surveyFlow.Reset();
                break;
            case WizardStep.AwaitingPin:
                // Cancel the pending pin. ConfirmPin clears PendingSurvey + transitions
                // back to Listening — same effect as a "cancel", just named after the
                // happy-path call site.
                _surveyFlow.ConfirmPin();
                break;
            case WizardStep.Gathering:
            case WizardStep.Done:
                // Route is in progress or done; the projector anchor is no longer safe
                // for new surveys (see position-anchor constraint). Full reset.
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
    }

    private void RecomputeStep()
    {
        if (!HasPickedMode)
        {
            CurrentStep = WizardStep.PickMode;
            return;
        }

        CurrentStep = _session.Mode == SessionMode.Motherlode
            ? WizardStep.MotherlodeMeasuring
            : _surveyFlow.CurrentState switch
            {
                SurveyFlowState.AwaitingPosition => WizardStep.AwaitingPosition,
                SurveyFlowState.Listening => WizardStep.Listening,
                SurveyFlowState.AwaitingPin => WizardStep.AwaitingPin,
                SurveyFlowState.Gathering => WizardStep.Gathering,
                SurveyFlowState.Done => WizardStep.Done,
                _ => WizardStep.AwaitingPosition,
            };
    }
}
