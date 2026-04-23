using System.Windows;
using Gorgon.Shared.Diagnostics;
using Gorgon.Shared.Logging;
using Gorgon.Shared.Modules;
using Gorgon.Shared.Settings;
using Microsoft.Extensions.Hosting;
using Samwise.Alarms;
using Samwise.Calibration;
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
        GrowthCalibrationService calibration,
        SettingsAutoSaver<SamwiseSettings> autoSaver,
        ModuleGates gates,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _state = state;
        _stateService = stateService;
        _diag = diag;
        _ = alarms;       // subscribes to state.PlotChanged in ctor
        _ = calibration;  // subscribes to state.PlotChanged in ctor
        _ = autoSaver;    // subscribes to SamwiseSettings.PropertyChanged in ctor
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
        // Hydrate must run on the UI thread: it raises PlotChanged for each
        // persisted plot, and the VM responds by mutating an ObservableCollection
        // already bound to the garden view's ListCollectionView.
        try
        {
            var loaded = await _stateService.LoadAsync(stoppingToken).ConfigureAwait(false);
            Dispatch(() => _state.Hydrate(loaded));
        }
        catch (Exception ex) { _diag?.Warn("Samwise", $"Failed to load state: {ex.Message}"); }

        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            var evt = _parser.TryParse(raw.Line, raw.Timestamp);
            if (evt is GardenEvent ge)
            {
                _diag?.Trace("Samwise.Parse", Describe(ge));
                Dispatch(() => _state.Apply(ge));
            }
        }
    }

    private static string Describe(GardenEvent e) => e switch
    {
        PlayerLogin pl => $"PlayerLogin  char={pl.CharName}",
        SetPetOwner spo => $"SetPetOwner  entity={spo.EntityId}",
        AppearanceLoop al => $"Appearance   model={al.ModelName}  scale={al.Scale:0.###}",
        UpdateDescription ud => $"UpdateDesc   plot={ud.PlotId}  title={ud.Title}  action={ud.Action}  scale={ud.Scale:0.###}",
        StartInteraction si => $"StartInter   plot={si.PlotId}  target={si.Target}",
        AddItem ai => $"AddItem      id={ai.ItemId}  name={ai.ItemName}",
        UpdateItemCode uic => $"UpdateItem   id={uic.ItemId}",
        GardeningXp => "GardeningXp",
        ScreenTextError => "ScreenError",
        PlantingCapReached pcr => $"PlantingCap  seed={pcr.SeedDisplayName}",
        _ => e.GetType().Name,
    };

    private static void Dispatch(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }
}
