using System.Windows;
using System.Windows.Threading;
using Arwen.Domain;
using Arwen.Parsing;
using Mithril.GameState.Inventory;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;
using Microsoft.Extensions.Hosting;

namespace Arwen.State;

/// <summary>
/// Eager Arwen ingestion that maintains the canonical per-character exact-favor
/// snapshots (<see cref="ArwenFavorState"/>) and feeds <see cref="CalibrationService"/>.
///
/// <para><b>Two ingestion channels (post-#608).</b>
/// <list type="bullet">
///   <item><b>L1 LocalPlayer pipe</b> — <c>ProcessStartInteraction</c>
///   (FavorUpdate) and <c>ProcessDeltaFavor</c>. These drive the gift-window
///   open/close and the per-NPC absolute-favor snapshot.</item>
///   <item><b>PlayerWorld bus</b> — <see cref="PlayerInventoryRemoved"/>
///   change events emitted by <see cref="PlayerInventoryStateService"/> after
///   the world's folder applies a <c>ProcessDeleteItem</c> frame. The
///   delete-side of the gift-detection FSM is fed from this bus, NOT from a
///   direct L1 <c>ProcessDeleteItem</c> parse — that's the #608 fix. The
///   inventory folder runs upstream of the bus subscriber in the world's
///   dispatch graph, so the change event always carries a resolved
///   <c>InternalName</c> (even under replay-from-session-start where the
///   pre-#608 L1-direct path would race the folder and silently drop the
///   gift).</item>
/// </list>
/// </para>
///
/// <para><b>Replay policy.</b> archetype-B
/// <see cref="ReplayMode.FromSessionStart"/> on the L1 side — rebuilds the
/// exact-favor snapshots and arms the gift-detection FSM by draining the
/// whole session backlog. The PlayerWorld bus side is a regular bus
/// subscription that observes the world's frame stream from the moment the
/// subscription attaches; the world's frame producer is itself an
/// archetype-A FromSessionStart consumer of the L1 driver, so it replays
/// inventory verbs on cold start and the change events flow through to this
/// subscriber in source-stream order.</para>
///
/// <para><b>Idempotence policy.</b> <em>None</em> from L1's perspective (no
/// <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/>). The
/// <see cref="CalibrationService"/> already owns per-key dedup at the sink
/// layer via its <c>_observationKeys</c> HashSet keyed
/// <c>SessionId|InstanceId|NpcKey|Item|Delta|Timestamp:O</c>, and
/// <see cref="ArwenFavorState.SetExactFavor"/> is an idempotent last-write-wins
/// upsert. An L1 high-water filter here would be redundant <em>and</em>
/// slightly wrong: a Mithril restart that re-reads the prior session must let
/// calibration see the in-session <c>DeleteItem</c>/<c>DeltaFavor</c> pair
/// again so the pending-correlation transient state machine can compute the
/// gift value; a sequence-based suppression would drop those replays and the
/// observation wouldn't land. See <see cref="CalibrationService"/> docstring
/// and #549's Arwen row for the audit.</para>
///
/// <para><b>Threading.</b> The L1 handler runs on the WPF dispatcher via
/// <see cref="DeliveryContext.Marshaled"/> — <see cref="ArwenFavorState.Favor"/>
/// is bound to UI, <c>_favorView.Save()</c> persists via per-character JSON,
/// and <see cref="FavorStateService.OnFavorUpdated"/> raises
/// <c>StateChanged</c>/<c>FavorChanged</c> consumed by bound view-models.
/// In test/headless contexts where <see cref="Application.Current"/> is null
/// we fall back to <see cref="DeliveryContext.Inline"/>. PlayerWorld bus
/// callbacks fire on the world's merger thread; we marshal calibration
/// mutations onto the dispatcher when one exists so transient FSM state
/// updates stay on the same thread the L1 handler uses (the FSM's pending
/// tuples aren't lock-guarded, mirroring the pre-#608 invariant).</para>
/// </summary>
public sealed class FavorIngestionService : BackgroundService
{
    private readonly ILogStreamDriver _driver;
    private readonly FavorLogParser _parser;
    private readonly FavorStateService _state;
    private readonly CalibrationService _calibration;
    private readonly PerCharacterView<ArwenFavorState> _favorView;
    private readonly IActiveCharacterService _activeChar;
    private readonly IPlayerWorld _playerWorld;
    private readonly IDiagnosticsSink? _diag;
    private readonly ModuleGate _gate;
    private ILogSubscription? _subscription;
    private IDisposable? _inventoryRemovedSubscription;
    private Dispatcher? _dispatcher;

    public FavorIngestionService(
        ILogStreamDriver driver,
        FavorLogParser parser,
        FavorStateService state,
        CalibrationService calibration,
        PerCharacterView<ArwenFavorState> favorView,
        IActiveCharacterService activeChar,
        SettingsAutoSaver<ArwenSettings> autoSaver,
        ModuleGates gates,
        IPlayerWorld playerWorld,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _state = state;
        _calibration = calibration;
        _favorView = favorView;
        _activeChar = activeChar;
        _playerWorld = playerWorld;
        _diag = diag;
        _ = autoSaver; // keep alive for PropertyChanged subscription
        _gate = gates.For("arwen");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Arwen.Ingestion", "Waiting for module gate…");
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        _diag?.Info("Arwen.Ingestion",
            "Gate opened — subscribing to L1 LocalPlayer pipe (favor verbs) and "
            + "PlayerWorld bus (PlayerInventoryRemoved) for gift detection");

        // Resolve UI dispatcher once. Headless/test contexts (no WPF App
        // running) get Inline delivery — the production path always has a
        // dispatcher because Arwen is Eager and the shell wires App before
        // any module gate opens.
        _dispatcher = Application.Current?.Dispatcher;
        var delivery = _dispatcher is null
            ? DeliveryContext.Inline
            : DeliveryContext.Marshaled(_dispatcher);

        // Bus subscription — the #608 delete channel. Subscribe before the L1
        // pump starts replaying so the calibration FSM observes Add+Delete
        // pairs that landed inside the same replay batch (the canonical
        // pre-#608 race scenario).
        _inventoryRemovedSubscription = _playerWorld.Bus.Subscribe<PlayerInventoryRemoved>(OnPlayerInventoryRemoved);

        _subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var ts = envelope.Payload.Timestamp;
                var evt = _parser.TryParse(envelope.Payload.Data, ts.UtcDateTime);
                if (evt is null) return ValueTask.CompletedTask;

                // ts is the log-line instant, TZ-correct via the L0 source clock
                // (#513) and stable across Mithril restarts. Plumb it through so
                // replay produces the same persisted GiftObservation.Timestamp
                // and CalibrationService's sink-layer dedup short-circuits.
                switch (evt)
                {
                    case FavorUpdate update:
                        _diag?.Trace("Arwen.Parse", $"FavorUpdate npc={update.NpcKey} favor={update.AbsoluteFavor:F1}");
                        _calibration.OnStartInteraction(update.NpcKey, ts);
                        var favor = _favorView.Current;
                        if (favor is not null)
                        {
                            favor.SetExactFavor(update.NpcKey, update.AbsoluteFavor, DateTimeOffset.UtcNow);
                            _favorView.Save();
                            _state.OnFavorUpdated(update.NpcKey);
                        }
                        break;

                    case FavorDelta delta:
                        _diag?.Trace("Arwen.Parse", $"FavorDelta npc={delta.NpcKey} delta={delta.Delta:F1}");
                        _calibration.OnDeltaFavor(delta.NpcKey, delta.Delta, ts);
                        break;

                    // ItemDeleted intentionally not handled here post-#608 —
                    // the delete signal arrives via PlayerWorld bus
                    // (PlayerInventoryRemoved), which carries the resolved
                    // InternalName from the inventory folder's upstream
                    // application of the same source line.
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = delivery,
                DiagnosticCategory = "Arwen.Ingestion",
                // No SkipProcessedHighWater — CalibrationService owns
                // per-key dedup at the sink layer; an L1 high-water filter
                // would suppress the in-session DeleteItem/DeltaFavor pair
                // that calibration must see on replay. See type doc.
            });

        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
            _inventoryRemovedSubscription?.Dispose();
            _inventoryRemovedSubscription = null;
        }
    }

    /// <summary>
    /// PlayerWorld bus handler for the inventory folder's
    /// <see cref="PlayerInventoryRemoved"/> change event. Fires on the world's
    /// merger thread; we marshal onto the WPF dispatcher when one exists so
    /// the calibration FSM's transient state (the same fields the L1 path
    /// touches) stays on a single thread — matches the pre-#608 invariant
    /// that the FSM's tuples don't need a lock.
    /// </summary>
    private void OnPlayerInventoryRemoved(Frame<PlayerInventoryRemoved> frame)
    {
        var payload = frame.Payload;
        var ts = new DateTimeOffset(payload.Timestamp, TimeSpan.Zero);

        void Dispatch() =>
            _calibration.OnItemDeleted(payload.InstanceId, payload.InternalName, ts);

        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            Dispatch();
        }
        else
        {
            // Async marshal — the L1 handler is also async-marshaled via the
            // DeliveryContext, so a synchronous Invoke here would risk
            // ordering pile-ups under bursty replay. The FSM's order is
            // event-time anyway; the dispatcher's queue preserves it
            // per-source for back-to-back posts.
            _dispatcher.InvokeAsync(Dispatch);
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _inventoryRemovedSubscription?.Dispose();
        _inventoryRemovedSubscription = null;
        base.Dispose();
    }
}
