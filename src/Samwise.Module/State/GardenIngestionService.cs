using System.Windows;
using Gorgon.Shared.Logging;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Settings;
using Microsoft.Extensions.Hosting;
using Samwise.Alarms;
using Samwise.Parsing;

namespace Samwise.State;

public sealed class GardenIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly GardenLogParser _parser;
    private readonly GardenStateMachine _state;
    private readonly GardenStateService _stateService;
    private readonly ModuleGate _gate;

    // These are pulled into the ctor so DI actually constructs them.
    // They attach to events inside their own ctors, so holding references here
    // is sufficient to keep them alive for the app lifetime.
    public GardenIngestionService(
        IPlayerLogStream stream,
        GardenLogParser parser,
        GardenStateMachine state,
        GardenStateService stateService,
        AlarmService alarms,
        SettingsAutoSaver<SamwiseSettings> autoSaver,
        ModuleGates gates)
    {
        _stream = stream;
        _parser = parser;
        _state = state;
        _stateService = stateService;
        _ = alarms;      // subscribes to state.PlotChanged in ctor
        _ = autoSaver;   // subscribes to SamwiseSettings.PropertyChanged in ctor
        _gate = gates.For("samwise");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);

        // Restore previous session before we start applying new events.
        try { await _stateService.LoadAsync(stoppingToken).ConfigureAwait(false); }
        catch { /* corrupt file → start fresh */ }

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            var evt = _parser.TryParse(raw.Line, raw.Timestamp);
            if (evt is GardenEvent ge)
            {
                Dispatch(() => _state.Apply(ge));
            }
        }
    }

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }
}
