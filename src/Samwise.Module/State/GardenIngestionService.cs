using System.Windows;
using Mithril.Shared.Diagnostics;
using Mithril.GameState.Inventory;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;
using Samwise.Alarms;
using Samwise.Calibration;
using Samwise.Parsing;

namespace Samwise.State;

/// <summary>
/// Bridges the L1 log driver and the <see cref="IInventoryService"/> event
/// feed into the <see cref="GardenStateMachine"/>. Post-#550 PR 3, the
/// Player.log path subscribes through <see cref="ILogStreamDriver"/> with
/// the archetype-B disposition from <a href="https://github.com/moumantai-gg/mithril/issues/549">#549</a>:
///
/// <list type="bullet">
///   <item><c>ReplayMode = FromSessionStart</c> — Samwise needs the full
///   session to rebuild in-flight crops mid-grow (<c>LiveOnly</c> would
///   lose them).</item>
///   <item><c>DeliveryContext = Marshaled(uiDispatcher)</c> — every
///   <c>Apply</c> raises <c>PlotChanged</c> → <see cref="ViewModels.GardenViewModel"/>'s
///   bound <c>ObservableCollection</c>. The L1 driver marshals onto the
///   UI thread; the hand-rolled <c>Application.Current?.Dispatcher</c>
///   helper is gone (#550 capability E).</item>
///   <item><c>SkipProcessedHighWater = persistedHighWater</c> — the
///   textbook persisted-state-vs-replay collision: <see cref="GardenStateService.LoadAllAsync"/>
///   hydrates plot state from per-character <c>samwise.json</c>, then L1
///   replays the entire session. Without the filter, plant /
///   <c>UpdateDescription</c> / <c>StartInteraction</c> / <c>GardeningXp</c>
///   events would re-apply on top of already-persisted plots, advancing
///   stages and burning slot caps. The <c>HandlePlant</c> plot-id
///   <c>ContainsKey</c> guard is partial; the high-water makes restart
///   semantics deterministic.</item>
/// </list>
///
/// <para><b>Inventory stream stays as-is.</b> <see cref="IInventoryService"/>
/// emits in-process service events (canonical inventory map fan-out from
/// the shared service), not an L1 log stream — the high-water filter does
/// not apply to it. <see cref="InventoryService.Subscribe"/> replays the
/// current map contents synchronously on the subscribing thread, closing
/// the late-attach race the way it always did. The Samwise-side IDs that
/// gate plant resolution come from inventory, not Player.log; once the
/// state machine processes a plant, the AddItem/DeleteItem pair that
/// originally fed it is irrelevant to restart-safety (the resolved crop
/// type lives in the persisted plot).</para>
///
/// <para><b>Containment retired.</b> The pre-L1 <c>ThrottledWarn</c> field,
/// ctor init, and per-message catch around the parse-and-Apply switch are
/// gone — L1 owns containment for every subscription via the driver's
/// rate-limited <c>Warn</c> + per-subscription fault state machine (#550
/// capabilities C + G). The <c>InventoryService</c> path keeps its own
/// in-process error surface (it never crossed L1).</para>
/// </summary>
public sealed class GardenIngestionService : BackgroundService
{
    private readonly ILogStreamDriver _driver;
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
        ILogStreamDriver driver,
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
        _driver = driver;
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
        _diag?.Info("Samwise", "Gate opened — loading persisted state and subscribing to L1 driver");

        // Restore previous session before we start applying new events.
        // HydrateCharacter must run on the UI thread: it raises PlotChanged for each
        // persisted plot, and the VM responds by mutating an ObservableCollection
        // already bound to the garden view's ListCollectionView.
        long persistedHighWater = 0L;
        try
        {
            var (loaded, highWater) = await _stateService.LoadAllAsync(stoppingToken).ConfigureAwait(false);
            persistedHighWater = highWater;
            DispatchHydrate(() =>
            {
                foreach (var (charName, plots) in loaded)
                    _state.HydrateCharacter(charName, plots);
            });
            _diag?.Info("Samwise",
                $"Hydrated {loaded.Count} character(s); high-water = {persistedHighWater} (L1 SkipProcessedHighWater)");
        }
        catch (Exception ex) { _diag?.Warn("Samwise", $"Failed to load state: {ex.Message}"); }

        // Inventory add/delete events are sourced from the shared IInventoryService
        // so Samwise picks up the same canonical map as Arwen, regardless of how
        // late this gate opens. Subscribe replays the current map contents
        // synchronously on this thread before going live, closing the race
        // where session-replay AddItem events fired before this gate opened
        // (and a plain += handler would have permanently missed them).
        //
        // This is a SERVICE-event consumer (in-process), NOT an L1 log
        // stream — the L1 high-water filter does not apply here. The inventory
        // service owns its own replay/idempotence shape. We marshal each
        // event onto the UI dispatcher to mirror what the L1 Marshaled
        // bridge does for the Player.log path, so PlotChanged → bound
        // ObservableCollection stays single-threaded.
        var subscription = _inventory.Subscribe(OnInventoryEvent);

        // L1 driver subscription for the Player.log payloads Samwise still owns
        // (everything except AddItem/DeleteItem, which come via IInventoryService).
        //
        // Disposition table from #549:
        //   ReplayMode            = FromSessionStart   (needs full session to rebuild in-flight crops)
        //   DeliveryContext       = Marshaled(UI)      (Apply → PlotChanged → bound ObservableCollection)
        //   SkipProcessedHighWater = persistedHighWater (textbook restart-safe dedup)
        //   DiagnosticCategory    = "Samwise.Ingestion" (replaces the retired ThrottledWarn bucket)
        //
        // The L0.5 router strips the "LocalPlayer:" envelope; the parser
        // consumes LocalPlayerLogLine.Data directly (no re-anchoring).
        var dispatcher = Application.Current?.Dispatcher;
        var deliveryContext = dispatcher is null
            ? DeliveryContext.Inline               // headless / tests — no dispatcher available
            : DeliveryContext.Marshaled(dispatcher);
        var logSubscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var line = envelope.Payload;
                var evt = _parser.TryParse(line.Data, line.Timestamp.UtcDateTime);
                if (evt is GardenEvent ge)
                {
                    _diag?.Trace("Samwise.Parse", Describe(ge));
                    _state.Apply(ge);
                    // Advance the persisted cursor only after a successful
                    // Apply — events that yielded no GardenEvent or threw
                    // (the driver swallows handler exceptions per #550 G)
                    // shouldn't be marked as processed. The state service
                    // takes Max() so out-of-order advances cannot regress.
                    _stateService.AdvanceHighWater(line.Sequence);
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = deliveryContext,
                SkipProcessedHighWater = persistedHighWater > 0 ? persistedHighWater : null,
                DiagnosticCategory = "Samwise.Ingestion",
            });

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            logSubscription.Dispose();
            subscription.Dispose();
        }
    }

    private void OnInventoryEvent(InventoryEvent evt)
    {
        var idStr = evt.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        GardenEvent? ge = evt.Kind switch
        {
            InventoryEventKind.Added => new AddItem(evt.Timestamp, idStr, evt.InternalName),
            InventoryEventKind.Deleted => new DeleteItem(evt.Timestamp, idStr),
            _ => null,
        };
        if (ge is null)
        {
            _diag?.Trace("Samwise.Parse", $"Skip InventoryEventKind.{evt.Kind} (not a garden-relevant event)");
            return;
        }
        _diag?.Trace("Samwise.Parse", Describe(ge));
        // The InventoryService replay + live feed comes off its own loop
        // thread, which doesn't satisfy the bound ObservableCollection's
        // thread affinity. Self-marshal here exactly the way the
        // pre-L1 ingestion did — this is NOT the L1 path, so L1's
        // Marshaled context doesn't cover it.
        DispatchInventory(() => _state.Apply(ge));
    }

    private static string Describe(GardenEvent e) => e switch
    {
        SetPetOwner spo => $"SetPetOwner  entity={spo.EntityId}",
        AppearanceLoop al => $"Appearance   model={al.ModelName}  scale={al.Scale:0.###}",
        UpdateDescription ud => $"UpdateDesc   plot={ud.PlotId}  title={ud.Title}  action={ud.Action}  scale={ud.Scale:0.###}",
        StartInteraction si => $"StartInter   plot={si.PlotId}  target={si.Target}",
        AddItem ai => $"AddItem      id={ai.ItemId}  name={ai.ItemName}",
        DeleteItem di => $"DeleteItem   id={di.ItemId}",
        UpdateItemCode uic => $"UpdateItem   id={uic.ItemId}",
        GardeningXp => "GardeningXp",
        ScreenTextError => "ScreenError",
        PlantingCapReached pcr => $"PlantingCap  seed={pcr.SeedDisplayName}",
        _ => e.GetType().Name,
    };

    /// <summary>
    /// Dispatcher hop for the one-shot hydration path. Same shape as the
    /// retired generic <c>Dispatch</c> helper — kept local to the
    /// hydration call because the L1 driver owns marshalling for the
    /// live path (capability E).
    /// </summary>
    private static void DispatchHydrate(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }

    /// <summary>
    /// Dispatcher hop for the <see cref="IInventoryService"/> event feed.
    /// L1 doesn't own this stream (it's an in-process service-event source,
    /// not a log subscription), so the consumer keeps the
    /// CheckAccess/InvokeAsync helper for inventory deliveries. Identical
    /// shape to the pre-L1 path; renamed only to make the L1 vs.
    /// non-L1 split obvious to a future reader.
    /// </summary>
    private static void DispatchInventory(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }
}
