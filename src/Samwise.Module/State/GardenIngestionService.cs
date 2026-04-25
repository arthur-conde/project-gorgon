using System.Windows;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Inventory;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;
using Samwise.Alarms;
using Samwise.Calibration;
using Samwise.Parsing;

namespace Samwise.State;

public sealed class GardenIngestionService : BackgroundService
{
    private readonly IPlayerLogStream _stream;
    private readonly IInventoryService _inventory;
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
        IInventoryService inventory,
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
        _inventory = inventory;
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
        // HydrateCharacter must run on the UI thread: it raises PlotChanged for each
        // persisted plot, and the VM responds by mutating an ObservableCollection
        // already bound to the garden view's ListCollectionView.
        try
        {
            var loaded = await _stateService.LoadAllAsync(stoppingToken).ConfigureAwait(false);
            Dispatch(() =>
            {
                foreach (var (charName, plots) in loaded)
                    _state.HydrateCharacter(charName, plots);
            });
        }
        catch (Exception ex) { _diag?.Warn("Samwise", $"Failed to load state: {ex.Message}"); }

        // Inventory add/delete events are sourced from the shared IInventoryService
        // so Samwise picks up the same canonical map as Arwen, regardless of how
        // late this gate opens. The events arrive on InventoryService's loop
        // thread; dispatch onto the UI thread to share the state machine.
        EventHandler<InventoryItem> onAdd = (_, item) =>
        {
            var ge = new AddItem(item.Timestamp, item.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture), item.InternalName);
            _diag?.Trace("Samwise.Parse", Describe(ge));
            Dispatch(() => _state.Apply(ge));
        };
        EventHandler<InventoryItem> onDelete = (_, item) =>
        {
            var ge = new DeleteItem(item.Timestamp, item.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _diag?.Trace("Samwise.Parse", Describe(ge));
            Dispatch(() => _state.Apply(ge));
        };
        _inventory.ItemAdded += onAdd;
        _inventory.ItemDeleted += onDelete;

        try
        {
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
        finally
        {
            _inventory.ItemAdded -= onAdd;
            _inventory.ItemDeleted -= onDelete;
        }
    }

    private static string Describe(GardenEvent e) => e switch
    {
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
