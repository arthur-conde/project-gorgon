using Mithril.Shared.Hotkeys;
using Legolas.Domain;
using Legolas.ViewModels;

namespace Legolas.Hotkeys;

public sealed class StartSessionCommand : IHotkeyCommand
{
    private readonly SessionState _session;
    public StartSessionCommand(SessionState session) => _session = session;
    public string Id => "legolas.session.start";
    public string DisplayName => "Start / Reset Session";
    public string? Category => "Legolas · Session";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _session.ClearSurveys();
        _session.PendingSurvey = null;
        _session.SurveyPhase = _session.HasPlayerPosition
            ? SurveyPhase.Surveying
            : SurveyPhase.Idle;
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
    private readonly SessionState _session;
    public SetPlayerPositionCommand(SessionState session) => _session = session;
    public string Id => "legolas.session.set_position";
    public string DisplayName => "Set Player Position";
    public string? Category => "Legolas · Session";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _session.PendingSurvey = null;
        _session.SurveyPhase = SurveyPhase.Idle;
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
