using System.Windows;
using Mithril.Shared.Diagnostics;
using Mithril.GameState.Inventory;
using Mithril.GameState.Skills;
using Mithril.Shared.Logging;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;
using Samwise.Alarms;
using Samwise.Calibration;
using Samwise.Parsing;

namespace Samwise.State;

/// <summary>
/// Bridges the L1 log driver, the <see cref="IPlayerWorld"/> inventory
/// change-event bus, and the <see cref="IPlayerSkillState"/> change channel
/// into the <see cref="GardenStateMachine"/>. Post-#550 PR 3, the Player.log
/// path subscribes through <see cref="ILogStreamDriver"/> with the archetype-B
/// disposition from <a href="https://github.com/moumantai-gg/mithril/issues/549">#549</a>:
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
///   <c>UpdateDescription</c> / <c>StartInteraction</c> events would
///   re-apply on top of already-persisted plots, advancing stages and
///   burning slot caps. The <c>HandlePlant</c> plot-id
///   <c>ContainsKey</c> guard is partial; the high-water makes restart
///   semantics deterministic. (Post-#581 the GardeningXp signal arrives
///   via <see cref="IPlayerSkillState"/>, not L1; the high-water still
///   covers the Player.log payloads Samwise still owns.)</item>
/// </list>
///
/// <para><b>Inventory channel — PlayerWorld-direct (#725 / #659 migration off
/// the <see cref="IInventoryView"/> shim).</b> The state machine's inventory
/// dependency is structurally a single-world concern: <see cref="HandleAddItem"/>
/// and the <c>DeleteItem</c> path read only <c>InstanceId</c> + <c>InternalName</c>
/// (the seed-map id↔crop ledger and the harvest-confirmation prefix match).
/// <see cref="GardenStateMachine"/> never reads <c>StackSize</c> or
/// <c>SizeConfirmed</c>, never composes Player.log adds with chat
/// <c>[Status] X xN added</c> observations, and the pre-migration handler
/// explicitly dropped the union shim's StackChanged events.
/// Per the principle 4 single-world-direct exit
/// (<c>docs/world-simulator.md</c> §"Views can compose across all three
/// categories"), we subscribe directly to
/// <see cref="IPlayerWorld.Bus"/>'s <see cref="PlayerInventoryAdded"/> and
/// <see cref="PlayerInventoryRemoved"/> change events. Same destination
/// <see cref="Legolas.Services.ItemCollectionTracker"/> picked in #699 → PR #721
/// after its own principle-4 audit.</para>
///
/// <para><b>Replay-on-subscribe is unnecessary under Call 1 + Call 2.</b> The
/// retired shim's late-attach atomic-replay contract (#585; the shim itself
/// retired in #659) guarded against frames published before the subscriber
/// attached. Under the eager-always Call 1 contract (#695 / PR #705) Samwise's
/// subscriptions attach inside <see cref="StartAsync"/> before the trailing
/// <c>WorldMergerStartHostedService</c> from Call 2 (#696 / PR #702) begins
/// draining frames, so no frames can have been dispatched by the time the bus
/// subscription is live. The three-surface view contract
/// (<c>docs/world-simulator.md</c> §"Three-surface contract for views")
/// explicitly omits a replay-on-subscribe primitive for the same reason. The
/// legacy <c>SubscribeAfterSeedAdd_StillResolvesPlant</c> regression's race
/// is by-construction eliminated here.</para>
///
/// <para><b>Containment retired.</b> The pre-L1 <c>ThrottledWarn</c> field,
/// ctor init, and per-message catch around the parse-and-Apply switch are
/// gone — L1 owns containment for every subscription via the driver's
/// rate-limited <c>Warn</c> + per-subscription fault state machine (#550
/// capabilities C + G). The bus does <em>not</em> swallow handler exceptions;
/// handlers must be exception-safe on the synchronous path because a throw
/// aborts the merger's per-frame dispatch loop (abandoning the remaining
/// subscriber snapshot for that frame). The current handlers only build a
/// value record and hand <see cref="GardenStateMachine.Apply"/> to the UI
/// dispatcher via <see cref="DispatchInventory"/>
/// (<see cref="System.Windows.Threading.Dispatcher.InvokeAsync(Action)"/> is
/// fire-and-forget on the merger thread), so any state-mutation throw lands
/// on the UI thread, not the bus path. Future inline work added to either
/// handler must hold that invariant or wrap the new code in a try/catch.</para>
///
/// <para><b>Gardening XP via <see cref="IPlayerSkillState.SubscribeChanges"/>.</b>
/// The harvest-confirmation signal (per
/// <see cref="GardenStateMachine.Apply"/>'s <c>GardeningXp</c> arm) is
/// sourced from the shared skill-state service rather than a Samwise-side
/// regex (#581). Same self-marshal shape as the inventory channel —
/// <see cref="IPlayerSkillState"/>'s callback fires on the tracker's
/// ingestion thread under its lock, not the bound ObservableCollection's
/// thread, so we hop via <see cref="DispatchInventory"/>.</para>
/// </summary>
public sealed class GardenIngestionService : BackgroundService
{
    private const string GardeningSkillKey = "Gardening";

    private readonly ILogStreamDriver _driver;
    private readonly IPlayerWorld _playerWorld;
    private readonly IPlayerSkillState _skillState;
    private readonly GardenLogParser _parser;
    private readonly GardenStateMachine _state;
    private readonly GardenStateService _stateService;
    private readonly IDiagnosticsSink? _diag;
    private ILogSubscription? _logSub;
    private IDisposable? _invAddedSub;
    private IDisposable? _invRemovedSub;
    private IDisposable? _skillSub;

    // These are pulled into the ctor so DI actually constructs them.
    // They attach to events inside their own ctors, so holding references here
    // is sufficient to keep them alive for the app lifetime.
    public GardenIngestionService(
        ILogStreamDriver driver,
        IPlayerWorld playerWorld,
        IPlayerSkillState skillState,
        GardenLogParser parser,
        GardenStateMachine state,
        GardenStateService stateService,
        AlarmService alarms,
        GrowthCalibrationService calibration,
        SettingsAutoSaver<SamwiseSettings> autoSaver,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _playerWorld = playerWorld;
        _skillState = skillState;
        _parser = parser;
        _state = state;
        _stateService = stateService;
        _diag = diag;
        _ = alarms;       // subscribes to state.PlotChanged in ctor
        _ = calibration;  // subscribes to state.PlotChanged in ctor
        _ = autoSaver;    // subscribes to SamwiseSettings.PropertyChanged in ctor

        if (diag is not null)
        {
            state.PlotChanged += (_, e) => diag.Info("Samwise.State",
                $"{e.Plot.CharName}/{e.Plot.PlotId} {e.OldStage?.ToString() ?? "-"} → {e.NewStage} ({e.Plot.CropType ?? "?"})");
        }
    }

    /// <summary>
    /// Eager subscription attach per Call 1 / principle eager-always (#695).
    /// The persisted-state hydrate + the three subscriptions (inventory,
    /// skill, L1 driver) all complete during the IHostedService chain's
    /// <c>StartAsync</c>, before the trailing world-merger drain starts
    /// (#702 / Call 2 ordering invariant). Samwise was already Eager so
    /// the gate-open at module-init opened immediately in production —
    /// the retirement here is a structural cleanup that makes the
    /// no-frames-missed contract uniform across Samwise + the lazy
    /// modules.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Samwise", "Loading persisted state and subscribing to L1 driver (eager attach)");

        // Restore previous session before we start applying new events.
        // HydrateCharacter must run on the UI thread: it raises PlotChanged for each
        // persisted plot, and the VM responds by mutating an ObservableCollection
        // already bound to the garden view's ListCollectionView.
        long persistedHighWater = 0L;
        try
        {
            var (loaded, highWater) = await _stateService.LoadAllAsync(cancellationToken).ConfigureAwait(false);
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

        // Inventory add/delete events come straight off IPlayerWorld.Bus
        // (principle 4 single-world-direct exit; see class-level remarks for
        // the audit and the #699 → #721 Legolas precedent that landed at the
        // same destination). Two typed subscriptions replace the legacy
        // union-shaped IInventoryView.Subscribe shim (retired in #659).
        //
        // No replay-on-subscribe primitive on the typed bus — the Call 1 +
        // Call 2 ordering invariant (#695 / #696) guarantees both
        // subscriptions are live before the world's merger drains any frames,
        // so no frames are missed. The seed-AddItem-before-plant race the
        // pre-#725 SubscribeAfterSeedAdd_StillResolvesPlant test pinned is
        // structurally impossible under the eager-always state-subscriber
        // contract.
        //
        // The bus dispatches on the world's merger thread, which doesn't
        // satisfy the bound ObservableCollection's thread affinity. Self-marshal
        // via DispatchInventory exactly the way the pre-#725 shim path did.
        _invAddedSub = _playerWorld.Bus.Subscribe<PlayerInventoryAdded>(OnPlayerInventoryAdded);
        _invRemovedSub = _playerWorld.Bus.Subscribe<PlayerInventoryRemoved>(OnPlayerInventoryRemoved);

        // Gardening XP arrival is the harvest-confirmation discriminator
        // (GardenStateMachine.HandleGardeningXp commits the staged plot
        // transition). Source: IPlayerSkillState's granular change channel,
        // not a raw-log regex (#581 — consumption-side rule from #578).
        //
        // SubscribeChanges is event-shaped (no replay), so each Gardening
        // delta arrives exactly once — matching the today-shape of one
        // ProcessUpdateSkill line → one GardeningXp event. SnapshotReplace
        // is deliberately ignored: today's regex only matched
        // ProcessUpdateSkill, not the periodic ProcessLoadSkills re-sync,
        // so a snapshot replay must not commit a harvest.
        //
        // The IPlayerSkillState callback fires on the tracker's ingestion
        // thread under its internal lock — same off-UI-thread shape as the
        // IPlayerWorld.Bus subscriptions above. Self-marshal via
        // DispatchInventory for the same reason: the GardenStateMachine.Apply
        // path raises PlotChanged → bound ObservableCollection which has
        // UI-thread affinity.
        _skillSub = _skillState.SubscribeChanges(OnSkillChange);

        // L1 driver subscription for the Player.log payloads Samwise still owns
        // (everything except AddItem/DeleteItem, which arrive via the
        // IPlayerWorld.Bus subscriptions above post-#725).
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
        _logSub = _driver.Subscribe<LocalPlayerLogLine>(
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

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _logSub?.Dispose();
            _invAddedSub?.Dispose();
            _invRemovedSub?.Dispose();
            _skillSub?.Dispose();
        }
    }

    private void OnPlayerInventoryAdded(Frame<PlayerInventoryAdded> frame)
    {
        var payload = frame.Payload;
        var idStr = payload.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ge = new AddItem(payload.Timestamp, idStr, payload.InternalName);
        _diag?.Trace("Samwise.Parse", Describe(ge));
        // The PlayerWorld bus dispatches on the merger thread, which doesn't
        // satisfy the bound ObservableCollection's thread affinity. Self-marshal
        // here exactly the way the pre-#725 shim path did — this is NOT the L1
        // driver path, so L1's Marshaled context doesn't cover it.
        DispatchInventory(() => _state.Apply(ge));
    }

    private void OnPlayerInventoryRemoved(Frame<PlayerInventoryRemoved> frame)
    {
        var payload = frame.Payload;
        var idStr = payload.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ge = new DeleteItem(payload.Timestamp, idStr);
        _diag?.Trace("Samwise.Parse", Describe(ge));
        DispatchInventory(() => _state.Apply(ge));
    }

    /// <summary>
    /// Bridge from <see cref="IPlayerSkillState.SubscribeChanges"/> into the
    /// state machine. Only Gardening Delta events matter — they are the
    /// confirmation that closes a pending harvest interaction (#581).
    ///
    /// <para>SnapshotReplace is deliberately ignored: today's parser only
    /// matched <c>ProcessUpdateSkill</c>, never the periodic
    /// <c>ProcessLoadSkills</c> re-sync, so a snapshot reconcile must not
    /// commit a harvest. Other skills are filtered out by the
    /// <c>SkillKey == "Gardening"</c> check.</para>
    ///
    /// <para>The XP value on the <see cref="SkillChange"/> is discarded — the
    /// state machine cares only that an update fired and at what timestamp,
    /// mirroring the prior regex path's payload-less <see cref="GardeningXp"/>
    /// event.</para>
    ///
    /// <para><b>Cold-start inter-drain race (acknowledged bound; tracked in
    /// <see href="https://github.com/moumantai-gg/mithril/issues/597">#597</see>):</b>
    /// <see cref="IPlayerSkillState.SubscribeChanges"/> is no-replay — a late
    /// subscriber sees only changes from the moment it attaches. The handler
    /// here attaches after the persisted-state hydrate (<c>LoadAllAsync</c>)
    /// inside <see cref="StartAsync"/>, while <c>PlayerSkillStateService</c>
    /// attaches earlier in the same chain and can drain L1 envelopes ahead
    /// of Samwise. Any Gardening <see cref="SkillChangeKind.Delta"/>
    /// fired in that pre-attach window is lost. Bound: harvests completed in
    /// that window stay <c>Ripe</c> with stale <c>_pendingHarvestPlotId</c>
    /// until the next live interaction, where <c>MarkOldestRipeOfType</c>
    /// recovers them. #597 closes this structurally via full-event-log replay
    /// on <c>SubscribeChanges</c> (sister to <see href="https://github.com/moumantai-gg/mithril/issues/585">#585</see>).</para>
    /// </summary>
    // internal for tests (Samwise.Tests has InternalsVisibleTo); production
    // callers go through SubscribeChanges in StartAsync above.
    internal void OnSkillChange(SkillChange change)
    {
        var ge = TryProjectGardeningXp(change);
        if (ge is null) return;

        _diag?.Trace("Samwise.Parse", Describe(ge));
        // The IPlayerSkillState callback fires on the L1 ingestion thread
        // under the tracker's internal lock — not on the UI dispatcher.
        // Self-marshal exactly like the inventory path so PlotChanged →
        // bound ObservableCollection stays single-threaded.
        DispatchInventory(() => _state.Apply(ge));
    }

    /// <summary>
    /// Pure decision: does this <see cref="SkillChange"/> count as a
    /// gardening-XP harvest-confirmation tick? Internal for direct unit
    /// testing — the dispatch wrapper above adds only the diagnostics
    /// + UI-thread hop on top of this filter.
    ///
    /// <list type="bullet">
    ///   <item><see cref="SkillChangeKind.Delta"/> only — today's regex
    ///   only matched <c>ProcessUpdateSkill</c>, not the periodic
    ///   <c>ProcessLoadSkills</c> re-sync, so a snapshot reconcile must
    ///   not commit a harvest.</item>
    ///   <item><see cref="SkillChange.SkillKey"/> must equal
    ///   <c>"Gardening"</c> — every other skill's delta is unrelated.</item>
    /// </list>
    /// </summary>
    internal static GardeningXp? TryProjectGardeningXp(SkillChange change)
    {
        if (change.Kind != SkillChangeKind.Delta) return null;
        if (!string.Equals(change.SkillKey, GardeningSkillKey, StringComparison.Ordinal)) return null;
        return new GardeningXp(change.Timestamp);
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
    /// Dispatcher hop for the <see cref="IPlayerWorld"/> inventory bus and
    /// the <see cref="IPlayerSkillState"/> change callback. L1 doesn't own
    /// either stream (the bus dispatches on the world merger's thread; the
    /// skill state callback runs on its tracker's ingestion thread under an
    /// internal lock), so the consumer keeps the CheckAccess/InvokeAsync
    /// helper for these deliveries. Identical shape to the pre-L1 path;
    /// renamed only to make the L1 vs. non-L1 split obvious to a future reader.
    /// </summary>
    private static void DispatchInventory(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.InvokeAsync(a);
    }
}
