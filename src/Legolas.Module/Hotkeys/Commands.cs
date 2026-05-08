using System.ComponentModel;
using Mithril.Shared.Hotkeys;
using Legolas.Domain;
using Legolas.Flow;
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

public sealed class SetPlayerPositionCommand : IHotkeyCommand
{
    private readonly SurveyFlowController _surveyFlow;
    public SetPlayerPositionCommand(SurveyFlowController surveyFlow) => _surveyFlow = surveyFlow;
    public string Id => "legolas.session.set_position";
    public string DisplayName => "Set Player Position";
    public string? Category => "Legolas · Session";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _surveyFlow.RequestSetPlayerPosition();
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
        // Forward the two SessionState signals that change whether a pin is
        // available to nudge. IsAnchorEditable is itself derived from
        // HasPlayerPosition + Surveys.Count (both already raise PropertyChanged
        // for IsAnchorEditable in SessionState), so subscribing to it covers
        // anchor placement, anchor sealing on first survey, and session reset.
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public string? Category => "Legolas · Pin Nudge";
    public HotkeyBinding? DefaultBinding => null;

    /// <summary>
    /// Pin Nudge only matters when there's actually a target to nudge:
    /// the anchor while it's still editable, or a selected survey pin.
    /// Outside both windows, arrow keys are dead weight that would otherwise
    /// be eaten system-wide (#139). The Execute body re-checks the same
    /// conditions, so a registration that briefly outraces a state change
    /// is harmless.
    /// </summary>
    public bool IsRegistrable
        => _session.IsAnchorEditable || _session.SelectedSurvey is not null;

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionState.IsAnchorEditable)
            or nameof(SessionState.SelectedSurvey))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRegistrable)));
        }
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
        var step = Step;

        var selected = _session.SelectedSurvey;
        if (selected is not null && selected.EffectivePixel.HasValue)
        {
            var p = selected.EffectivePixel.Value;
            _map.CorrectSurveyCommand.Execute(
                new CorrectionArgs(selected, new PixelPoint(p.X + dx * step, p.Y + dy * step)));
            return Task.CompletedTask;
        }

        // No pin selected: fall through to the anchor while it's still editable
        // (post-Set Position, pre-first-survey). Once a survey lands the anchor
        // becomes load-bearing for projection and IsAnchorEditable flips false.
        if (_session.IsAnchorEditable)
        {
            var p = _session.PlayerPosition;
            _map.MoveAnchor(new PixelPoint(p.X + dx * step, p.Y + dy * step));
        }
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
