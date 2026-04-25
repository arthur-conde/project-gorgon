using Mithril.Shared.Hotkeys;
using Samwise.Alarms;
using Samwise.State;

namespace Samwise.Hotkeys;

public sealed class SnoozeAllAlarmsCommand : IHotkeyCommand
{
    private readonly AlarmService _alarms;
    public SnoozeAllAlarmsCommand(AlarmService alarms) { _alarms = alarms; }
    public string Id => "samwise.snooze";
    public string DisplayName => "Snooze all alarms";
    public string? Category => "Samwise · Garden";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken ct) { _alarms.SnoozeAll(); return Task.CompletedTask; }
}

public sealed class DismissAllAlarmsCommand : IHotkeyCommand
{
    private readonly AlarmService _alarms;
    public DismissAllAlarmsCommand(AlarmService alarms) { _alarms = alarms; }
    public string Id => "samwise.dismiss";
    public string DisplayName => "Dismiss all alarms";
    public string? Category => "Samwise · Garden";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken ct) { _alarms.DismissAll(); return Task.CompletedTask; }
}

public sealed class MarkOldestRipeHarvestedCommand : IHotkeyCommand
{
    private readonly GardenStateMachine _state;
    public MarkOldestRipeHarvestedCommand(GardenStateMachine state) { _state = state; }
    public string Id => "samwise.mark-oldest-ripe";
    public string DisplayName => "Mark oldest ripe plot harvested";
    public string? Category => "Samwise · Garden";
    public HotkeyBinding? DefaultBinding => null;
    public Task ExecuteAsync(CancellationToken ct)
    {
        Plot? oldest = null;
        foreach (var (_, plots) in _state.Snapshot())
            foreach (var p in plots.Values)
                if (p.Stage == PlotStage.Ripe && (oldest is null || p.UpdatedAt < oldest.UpdatedAt))
                    oldest = p;
        if (oldest is not null)
        {
            oldest.Stage = PlotStage.Harvested;
            oldest.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }
}
