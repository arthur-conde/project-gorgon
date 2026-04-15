using System.Windows;
using Gorgon.Shared.Diagnostics;
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
    private readonly IDiagnosticsSink? _diag;
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
        ModuleGates gates,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _state = state;
        _stateService = stateService;
        _diag = diag;
        _ = alarms;      // subscribes to state.PlotChanged in ctor
        _ = autoSaver;   // subscribes to SamwiseSettings.PropertyChanged in ctor
        _gate = gates.For("samwise");

        if (diag is not null)
        {
            state.PlotChanged += (_, e) => diag.Info("Samwise.State",
                $"{e.Plot.CharName}/{e.Plot.PlotId} {e.OldStage?.ToString() ?? "-"} → {e.NewStage} ({e.Plot.CropType ?? "?"})");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Samwise", "Waiting for module gate…");
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        _diag?.Info("Samwise", "Gate opened — loading persisted state and subscribing to Player.log");

        // Restore previous session before we start applying new events.
        try { await _stateService.LoadAsync(stoppingToken).ConfigureAwait(false); }
        catch (Exception ex) { _diag?.Warn("Samwise", $"Failed to load state: {ex.Message}"); }

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            var evt = _parser.TryParse(raw.Line, raw.Timestamp);
            if (evt is GardenEvent ge)
            {
                _diag?.Trace("Samwise.Parse", ge.GetType().Name);
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
