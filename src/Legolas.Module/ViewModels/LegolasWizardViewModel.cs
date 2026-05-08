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
    AwaitingPosition,
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
        _motherlodeFlow.PropertyChanged += OnMotherlodeFlowChanged;
        _session.PropertyChanged += OnSessionChanged;
        if (_reportService is not null)
            _reportService.ReportGenerated += OnReportGenerated;
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
        // Entering AwaitingPosition auto-opens the map overlay so the user has
        // something to click for setting player position.
        if (value == WizardStep.AwaitingPosition)
            _session.IsMapVisible = true;

        // Entering Listening / Gathering auto-opens the inventory overlay.
        // Listening: user is picking the leftmost survey to use — they need the
        // bag visible to see which one. Gathering: the queue serves as a
        // walk-the-route checklist.
        if (value is WizardStep.Listening or WizardStep.Gathering)
            _session.IsInventoryVisible = true;
    }

    /// <summary>Headline displayed inline with the wizard's per-step nav row.</summary>
    public string CurrentStepTitle => CurrentStep switch
    {
        WizardStep.PickMode => "What are you doing?",
        WizardStep.AwaitingPosition => "Show me where you are",
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

        CurrentStep = _session.Mode == SessionMode.Motherlode
            ? WizardStep.MotherlodeMeasuring
            : _surveyFlow.CurrentState switch
            {
                SurveyFlowState.AwaitingPosition => WizardStep.AwaitingPosition,
                // Ready and Listening project to the same wizard step. The
                // existing Listening UI block already adapts cleanly to an
                // empty Surveys list (instructions stay forward-looking, the
                // pin list shows "0 placed", the Go! button gates on
                // CanOptimize, and the IsAnchorEditable tip surfaces only
                // while no pins have landed) — so promoting Ready to its own
                // wizard step would just add noise without changing what the
                // user sees.
                SurveyFlowState.Ready => WizardStep.Listening,
                SurveyFlowState.Listening => WizardStep.Listening,
                SurveyFlowState.Gathering => WizardStep.Gathering,
                SurveyFlowState.Done => WizardStep.Done,
                _ => WizardStep.AwaitingPosition,
            };
    }
}
