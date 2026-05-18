using System.ComponentModel;
using Mithril.Shared.Hotkeys;
using Legolas.Diagnostics;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;

namespace Legolas.Hotkeys;

public sealed class StartSessionCommand : IHotkeyCommand
{
    private readonly SurveyFlowController _surveyFlow;
    public StartSessionCommand(SurveyFlowController surveyFlow) => _surveyFlow = surveyFlow;
    public string Id => "legolas.session.start";
    public string DisplayName => "Start / Reset Session";
    public string? Category => "Legolas · Session";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _surveyFlow.Reset();
        return Task.CompletedTask;
    }
}

public sealed class MarkCurrentCollectedCommand : IHotkeyCommand
{
    private readonly SessionState _session;
    public MarkCurrentCollectedCommand(SessionState session) => _session = session;
    public string Id => "legolas.session.mark_collected";
    public string DisplayName => "Mark Current Survey Collected";
    public string? Category => "Legolas · Session";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var target = _session.Surveys.FirstOrDefault(s => s.IsActiveTarget)
                  ?? _session.Surveys.Where(s => !s.Collected).OrderBy(s => s.RouteOrder ?? int.MaxValue).FirstOrDefault();
        if (target is null) return Task.CompletedTask;
        target.UpdateModel(target.Model with { Collected = true });
        _session.LastLogEvent = $"Manually marked: {target.Name}";
        return Task.CompletedTask;
    }
}

public sealed class SetSurveyModeCommand : IHotkeyCommand
{
    private readonly SessionState _session;
    public SetSurveyModeCommand(SessionState session) => _session = session;
    public string Id => "legolas.mode.survey";
    public string DisplayName => "Mode: Survey";
    public string? Category => "Legolas · Mode";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _session.Mode = SessionMode.Survey;
        return Task.CompletedTask;
    }
}

public sealed class SetMotherlodeModeCommand : IHotkeyCommand
{
    private readonly SessionState _session;
    public SetMotherlodeModeCommand(SessionState session) => _session = session;
    public string Id => "legolas.mode.motherlode";
    public string DisplayName => "Mode: Motherlode";
    public string? Category => "Legolas · Mode";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _session.Mode = SessionMode.Motherlode;
        return Task.CompletedTask;
    }
}

public sealed class ToggleMapOverlayCommand : IHotkeyCommand
{
    private readonly SessionState _session;
    public ToggleMapOverlayCommand(SessionState session) => _session = session;
    public string Id => "legolas.overlay.map.toggle";
    public string DisplayName => "Toggle Map Overlay";
    public string? Category => "Legolas · Overlay";
    public HotkeyBinding? DefaultBinding => null;
    // Stays registered while alt-tabbed so the user can peek at the map from a browser.
    public bool RespectsFocusGate => false;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _session.IsMapVisible = !_session.IsMapVisible;
        return Task.CompletedTask;
    }
}

public sealed class ToggleInventoryOverlayCommand : IHotkeyCommand
{
    private readonly SessionState _session;
    public ToggleInventoryOverlayCommand(SessionState session) => _session = session;
    public string Id => "legolas.overlay.inventory.toggle";
    public string DisplayName => "Toggle Inventory Overlay";
    public string? Category => "Legolas · Overlay";
    public HotkeyBinding? DefaultBinding => null;
    // Stays registered while alt-tabbed so the user can peek at inventory from a browser.
    public bool RespectsFocusGate => false;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _session.IsInventoryVisible = !_session.IsInventoryVisible;
        return Task.CompletedTask;
    }
}

public sealed class ToggleCalibrationOverlayCommand : IHotkeyCommand
{
    private readonly SessionState _session;
    public ToggleCalibrationOverlayCommand(SessionState session) => _session = session;
    public string Id => "legolas.overlay.calibration.toggle";
    public string DisplayName => "Toggle Map Calibration";
    public string? Category => "Legolas · Overlay";
    public HotkeyBinding? DefaultBinding => null;
    // Calibration is done with the in-game map open, so it must stay registered
    // while the game (not Mithril) has focus — same as the other overlays.
    public bool RespectsFocusGate => false;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _session.IsCalibrationVisible = !_session.IsCalibrationVisible;
        return Task.CompletedTask;
    }
}

public sealed class OptimizeRouteCommand : IHotkeyCommand
{
    private readonly MapOverlayViewModel _map;
    public OptimizeRouteCommand(MapOverlayViewModel map) => _map = map;
    public string Id => "legolas.route.optimize";
    public string DisplayName => "Optimize Route";
    public string? Category => "Legolas · Session";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_map.OptimizeRouteCommand.CanExecute(null))
        {
            _map.OptimizeRouteCommand.Execute(null);
        }
        return Task.CompletedTask;
    }
}

public sealed class ToggleMapClickThroughCommand : IHotkeyCommand
{
    private readonly LegolasSettings _settings;
    public ToggleMapClickThroughCommand(LegolasSettings settings) => _settings = settings;
    public string Id => "legolas.overlay.map.clickthrough.toggle";
    public string DisplayName => "Toggle Map Click-Through";
    public string? Category => "Legolas · Overlay";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _settings.ClickThroughMap = !_settings.ClickThroughMap;
        return Task.CompletedTask;
    }
}

public sealed class ToggleInventoryClickThroughCommand : IHotkeyCommand
{
    private readonly LegolasSettings _settings;
    public ToggleInventoryClickThroughCommand(LegolasSettings settings) => _settings = settings;
    public string Id => "legolas.overlay.inventory.clickthrough.toggle";
    public string DisplayName => "Toggle Inventory Click-Through";
    public string? Category => "Legolas · Overlay";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _settings.ClickThroughInventory = !_settings.ClickThroughInventory;
        return Task.CompletedTask;
    }
}

public sealed class ToggleAllOverlaysCommand : IHotkeyCommand
{
    private readonly SessionState _session;
    public ToggleAllOverlaysCommand(SessionState session) => _session = session;
    public string Id => "legolas.overlay.all.toggle";
    public string DisplayName => "Toggle All Overlays";
    public string? Category => "Legolas · Overlay";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var anyVisible = _session.IsMapVisible || _session.IsInventoryVisible;
        var target = !anyVisible;
        _session.IsMapVisible = target;
        _session.IsInventoryVisible = target;
        return Task.CompletedTask;
    }
}

public sealed class ToggleBearingWedgesCommand : IHotkeyCommand
{
    private readonly SessionState _session;
    public ToggleBearingWedgesCommand(SessionState session) => _session = session;
    public string Id => "legolas.overlay.wedges.toggle";
    public string DisplayName => "Toggle Bearing Wedges";
    public string? Category => "Legolas · Overlay";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _session.ShowBearingWedges = !_session.ShowBearingWedges;
        return Task.CompletedTask;
    }
}

// ─── Pin Nudge ───────────────────────────────────────────────────────────────
// 12 hotkey commands (4 directions × 3 step tiers) so the user can rebind any
// combination — including just one tier and skipping the others. Step
// magnitudes live in LegolasSettings (NudgeStepDefault / Fast / Fine), read
// fresh on every Execute so settings changes apply immediately. Default
// bindings are intentionally null because arrow keys collide with in-game
// movement; users must opt-in via the Hotkeys settings UI.

public abstract class NudgePinCommandBase : IGatedHotkeyCommand
{
    private readonly SessionState _session;
    private readonly MapOverlayViewModel _map;
    private readonly LegolasSettings _settings;

    protected NudgePinCommandBase(SessionState session, MapOverlayViewModel map, LegolasSettings settings)
    {
        _session = session;
        _map = map;
        _settings = settings;
        // Pin Nudge targets, in precedence order (see MapOverlayViewModel.Nudge):
        // a selected #477A calibration marker, the selected survey pin, or the
        // #477C manual player anchor. Registrability tracks all three inputs.
        _session.PropertyChanged += OnSessionPropertyChanged;
        _map.PropertyChanged += OnMapPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public string? Category => "Legolas · Pin Nudge";
    public HotkeyBinding? DefaultBinding => null;

    /// <summary>
    /// Pin Nudge matters when there's something to nudge: a selected survey
    /// pin, a selected #477A calibration marker, or the #477C manual player
    /// anchor. Outside those windows arrow keys are dead weight that would
    /// otherwise be eaten system-wide (#139). The Execute body re-checks, so a
    /// registration that briefly outraces a state change is harmless.
    /// </summary>
    public bool IsRegistrable =>
        _session.SelectedSurvey is not null
        || _map.HasSelectedCalibrationMarker
        || (_session.Mode == SessionMode.Survey
            && _session.SurveyPlayerIsManual
            && _session.SurveyPlayerPixel is not null);

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionState.SelectedSurvey)
                           or nameof(SessionState.SurveyPlayerIsManual)
                           or nameof(SessionState.SurveyPlayerPixel)
                           or nameof(SessionState.Mode))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRegistrable)));
    }

    private void OnMapPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapOverlayViewModel.HasSelectedCalibrationMarker))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRegistrable)));
    }

    /// <summary>Unit vector for the direction this command moves a pin.</summary>
    protected abstract (double Dx, double Dy) Direction { get; }

    /// <summary>Step magnitude (read from settings on each call).</summary>
    protected abstract double Step { get; }

    protected double NudgeStepDefault => _settings.NudgeStepDefault;
    protected double NudgeStepFast => _settings.NudgeStepFast;
    protected double NudgeStepFine => _settings.NudgeStepFine;

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var (dx, dy) = Direction;
        // Single path: MapOverlayViewModel.Nudge applies the #477A marker /
        // survey / #477C manual-anchor precedence, so the keyboard hotkeys and
        // the on-screen nudge pad can't diverge.
        _map.Nudge(dx, dy, Step);
        return Task.CompletedTask;
    }
}

public sealed class NudgePinUpCommand : NudgePinCommandBase
{
    public NudgePinUpCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.up";
    public override string DisplayName => "Nudge Pin Up";
    protected override (double, double) Direction => (0, -1);
    protected override double Step => NudgeStepDefault;
}

public sealed class NudgePinUpFastCommand : NudgePinCommandBase
{
    public NudgePinUpFastCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.up.fast";
    public override string DisplayName => "Nudge Pin Up (Fast)";
    protected override (double, double) Direction => (0, -1);
    protected override double Step => NudgeStepFast;
}

public sealed class NudgePinUpFineCommand : NudgePinCommandBase
{
    public NudgePinUpFineCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.up.fine";
    public override string DisplayName => "Nudge Pin Up (Fine)";
    protected override (double, double) Direction => (0, -1);
    protected override double Step => NudgeStepFine;
}

public sealed class NudgePinDownCommand : NudgePinCommandBase
{
    public NudgePinDownCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.down";
    public override string DisplayName => "Nudge Pin Down";
    protected override (double, double) Direction => (0, 1);
    protected override double Step => NudgeStepDefault;
}

public sealed class NudgePinDownFastCommand : NudgePinCommandBase
{
    public NudgePinDownFastCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.down.fast";
    public override string DisplayName => "Nudge Pin Down (Fast)";
    protected override (double, double) Direction => (0, 1);
    protected override double Step => NudgeStepFast;
}

public sealed class NudgePinDownFineCommand : NudgePinCommandBase
{
    public NudgePinDownFineCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.down.fine";
    public override string DisplayName => "Nudge Pin Down (Fine)";
    protected override (double, double) Direction => (0, 1);
    protected override double Step => NudgeStepFine;
}

public sealed class NudgePinLeftCommand : NudgePinCommandBase
{
    public NudgePinLeftCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.left";
    public override string DisplayName => "Nudge Pin Left";
    protected override (double, double) Direction => (-1, 0);
    protected override double Step => NudgeStepDefault;
}

public sealed class NudgePinLeftFastCommand : NudgePinCommandBase
{
    public NudgePinLeftFastCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.left.fast";
    public override string DisplayName => "Nudge Pin Left (Fast)";
    protected override (double, double) Direction => (-1, 0);
    protected override double Step => NudgeStepFast;
}

public sealed class NudgePinLeftFineCommand : NudgePinCommandBase
{
    public NudgePinLeftFineCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.left.fine";
    public override string DisplayName => "Nudge Pin Left (Fine)";
    protected override (double, double) Direction => (-1, 0);
    protected override double Step => NudgeStepFine;
}

public sealed class NudgePinRightCommand : NudgePinCommandBase
{
    public NudgePinRightCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.right";
    public override string DisplayName => "Nudge Pin Right";
    protected override (double, double) Direction => (1, 0);
    protected override double Step => NudgeStepDefault;
}

public sealed class NudgePinRightFastCommand : NudgePinCommandBase
{
    public NudgePinRightFastCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.right.fast";
    public override string DisplayName => "Nudge Pin Right (Fast)";
    protected override (double, double) Direction => (1, 0);
    protected override double Step => NudgeStepFast;
}

public sealed class NudgePinRightFineCommand : NudgePinCommandBase
{
    public NudgePinRightFineCommand(SessionState s, MapOverlayViewModel m, LegolasSettings c) : base(s, m, c) { }
    public override string Id => "legolas.pin.nudge.right.fine";
    public override string DisplayName => "Nudge Pin Right (Fine)";
    protected override (double, double) Direction => (1, 0);
    protected override double Step => NudgeStepFine;
}

// ─── Diagnostics ─────────────────────────────────────────────────────────────
// Frame-time profiling. All hidden behind ShellSettings.DeveloperMode in the
// Hotkeys settings UI (IsDeveloperOnly = true). Pin count for the synthetic
// load is read from LegolasSettings.PerfHarnessPinCount so a single command
// covers the 30-pin median and 100-pin tail cases. Reports land in
// %LocalAppData%/Mithril/Legolas/perf/.

/// <summary>
/// Toggle the live frame-time logger. Use during a real survey session (with
/// PG running) to capture what the user actually feels. Stop writes a CSV +
/// summary into the perf folder. Safe to leave bound; it's free when not running.
/// </summary>
public sealed class ToggleFrameTimeLoggerCommand : IHotkeyCommand
{
    private readonly FrameTimeLogger _logger;
    private readonly LegolasSettings _settings;
    private readonly SessionState _session;
    private readonly SurveyFlowController _surveyFlow;
    public ToggleFrameTimeLoggerCommand(
        FrameTimeLogger logger,
        LegolasSettings settings,
        SessionState session,
        SurveyFlowController surveyFlow)
    {
        _logger = logger;
        _settings = settings;
        _session = session;
        _surveyFlow = surveyFlow;
    }
    public string Id => "legolas.diag.frame_logger.toggle";
    public string DisplayName => "Toggle Frame-Time Logger";
    public string? Category => "Legolas · Diagnostics";
    public HotkeyBinding? DefaultBinding => null;
    public bool RespectsFocusGate => false;
    public bool IsDeveloperOnly => true;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_logger.IsRunning)
        {
            _logger.Stop();
            _session.LastLogEvent = $"Frame logger stopped → {_logger.OutputDirectory}";
        }
        else
        {
            var cfg = new FrameRunConfig(
                PinCount: _session.Surveys.Count,
                ActiveTreatment: _settings.ActivePinStyle.Treatment.ToString(),
                AllowsTransparency: true,
                ClickThroughMap: _settings.ClickThroughMap,
                ShowBearingWedges: _session.ShowBearingWedges,
                ShowRouteLines: _session.ShowRouteLines,
                MapWidth: _settings.MapOverlay.Width,
                MapHeight: _settings.MapOverlay.Height,
                FsmState: _surveyFlow.CurrentState.ToString());
            _logger.Start("manual", cfg);
            _session.LastLogEvent = "Frame logger started — press hotkey again to stop and write report.";
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Run the synthetic perf harness once with the current active-pin treatment.
/// Pin count comes from <see cref="LegolasSettings.PerfHarnessPinCount"/>.
/// Resets the session, anchors at the map centre, injects deterministic
/// surveys, captures Listening then Gathering, writes paired reports.
/// </summary>
public sealed class RunSurveyPerfHarnessCommand : IHotkeyCommand
{
    private readonly SurveyPerfHarness _harness;
    private readonly SessionState _session;
    private readonly LegolasSettings _settings;
    public RunSurveyPerfHarnessCommand(SurveyPerfHarness harness, SessionState session, LegolasSettings settings)
    {
        _harness = harness;
        _session = session;
        _settings = settings;
    }
    public string Id => "legolas.diag.perf_harness.run";
    public string DisplayName => "Run Survey Perf Harness";
    public string? Category => "Legolas · Diagnostics";
    public HotkeyBinding? DefaultBinding => null;
    public bool RespectsFocusGate => false;
    public bool IsDeveloperOnly => true;
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_harness.IsRunning) return;
        var pins = _settings.PerfHarnessPinCount;
        _session.LastLogEvent = $"Perf harness ({pins} pins) running — Listening 15s → Gathering 15s.";
        try
        {
            await _harness.RunAsync(pinCount: pins, phaseSeconds: 15, ct: cancellationToken).ConfigureAwait(false);
            _session.LastLogEvent = "Perf harness complete — see %LocalAppData%/Mithril/Legolas/perf/";
        }
        catch (OperationCanceledException)
        {
            _session.LastLogEvent = "Perf harness cancelled.";
        }
    }
}

/// <summary>
/// Run the harness back-to-back across active-pin treatments (Halo, Glow) so
/// a single keypress produces matched A/B reports without flipping settings
/// between runs. Pin count from <see cref="LegolasSettings.PerfHarnessPinCount"/>.
/// Produces 4 reports per invocation; restores the original treatment when done.
/// ~65 seconds total.
/// </summary>
public sealed class RunSurveyPerfHarnessTreatmentSweepCommand : IHotkeyCommand
{
    private readonly SurveyPerfHarness _harness;
    private readonly SessionState _session;
    private readonly LegolasSettings _settings;
    public RunSurveyPerfHarnessTreatmentSweepCommand(SurveyPerfHarness harness, SessionState session, LegolasSettings settings)
    {
        _harness = harness;
        _session = session;
        _settings = settings;
    }
    public string Id => "legolas.diag.perf_harness.sweep";
    public string DisplayName => "Run Perf Harness — Treatment Sweep (Halo+Glow)";
    public string? Category => "Legolas · Diagnostics";
    public HotkeyBinding? DefaultBinding => null;
    public bool RespectsFocusGate => false;
    public bool IsDeveloperOnly => true;
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_harness.IsRunning) return;
        var pins = _settings.PerfHarnessPinCount;
        _session.LastLogEvent = $"Perf sweep ({pins} pins) running — ~65s.";
        try
        {
            await _harness.RunTreatmentSweepAsync(pins, phaseSeconds: 15, cancellationToken).ConfigureAwait(false);
            _session.LastLogEvent = $"Perf sweep ({pins} pins) complete — 4 reports in perf folder.";
        }
        catch (OperationCanceledException)
        {
            _session.LastLogEvent = "Perf sweep cancelled.";
        }
    }
}

// ─── Calibration (#477A) ─────────────────────────────────────────────────────
// Optional bindable mirrors of the wizard-panel phase trigger / Confirm, for
// users who don't want to look away from the game. No default binding (the
// Legolas convention — game-movement collision avoidance). Gated so the keys
// aren't eaten system-wide outside an armed walkthrough.

/// <summary>Flip the guided calibration walkthrough between the Drop and Pair
/// phases (mirror of the wizard-panel button).</summary>
public sealed class ToggleCalibrationPhaseCommand : IGatedHotkeyCommand
{
    private readonly PinCalibrationCoordinator _cal;

    public ToggleCalibrationPhaseCommand(PinCalibrationCoordinator cal)
    {
        _cal = cal;
        _cal.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PinCalibrationCoordinator.IsArmed))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRegistrable)));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id => "legolas.calibration.phase.toggle";
    public string DisplayName => "Calibration: Toggle Drop/Pair Phase";
    public string? Category => "Legolas · Calibration";
    public HotkeyBinding? DefaultBinding => null;
    public bool IsRegistrable => _cal.IsArmed;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _cal.TogglePhase();
        return Task.CompletedTask;
    }
}

/// <summary>Terminal Confirm of the guided calibration (mirror of the
/// wizard-panel Confirm; gated on ≥3 pairs and a good residual — "finish
/// anyway" stays panel-only so a stray keypress can't persist a loose fit).</summary>
public sealed class ConfirmCalibrationCommand : IGatedHotkeyCommand
{
    private readonly PinCalibrationCoordinator _cal;

    public ConfirmCalibrationCommand(PinCalibrationCoordinator cal)
    {
        _cal = cal;
        _cal.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PinCalibrationCoordinator.IsArmed)
                               or nameof(PinCalibrationCoordinator.CanConfirm)
                               or nameof(PinCalibrationCoordinator.IsResidualGood))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRegistrable)));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id => "legolas.calibration.confirm";
    public string DisplayName => "Calibration: Confirm";
    public string? Category => "Legolas · Calibration";
    public HotkeyBinding? DefaultBinding => null;
    public bool IsRegistrable => _cal.IsArmed && _cal.CanConfirm && _cal.IsResidualGood;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _cal.Confirm();
        return Task.CompletedTask;
    }
}
