using System.Windows;
using System.Windows.Threading;
using Arwen.Domain;
using Arwen.Parsing;
using Mithril.GameState.Gifting;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;

namespace Arwen.State;

/// <summary>
/// Eager Arwen ingestion. Two channels, both archetype-B
/// <see cref="ReplayMode.FromSessionStart"/>:
/// <list type="bullet">
///   <item><b>L1 LocalPlayer pipe</b> — drives the per-character exact-favor
///   snapshot off <see cref="FavorUpdate"/> (parsed from
///   <c>ProcessStartInteraction</c>). This is the only L1 signal the
///   ingestion path consumes; the gift-detection FSM lives in
///   <see cref="IGiftSignalService"/> (see below).</item>
///   <item><b><see cref="IGiftSignalService"/></b> — the Tier-2 lift of
///   Arwen's gift-detection FSM. The signal service owns a single L1
///   subscription with its own <c>ProcessAddItem</c> map, correlates the
///   <c>ProcessStartInteraction</c> / <c>ProcessDeleteItem</c> /
///   <c>ProcessDeltaFavor</c> verb triple inside one pump, and emits a
///   <see cref="GiftAccepted"/> with the resolved <c>InternalName</c> baked
///   in. The React-channel <see cref="IGiftSignalService.Subscribe"/>
///   contract atomically replays the full in-session event log to late
///   subscribers (#585 contract), so attach order vs the world's frame
///   producer is irrelevant — Arwen never observes a cross-pump
///   resolved-name race regardless of when the gate opens.</item>
/// </list>
///
/// <para><b>Why not a direct PlayerWorld bus subscription (#608, iteration
/// 1).</b> Earlier iterations of #608 had this service subscribe to
/// <c>IPlayerWorld.Bus.Subscribe&lt;PlayerInventoryRemoved&gt;</c> for the
/// delete signal. That fixed the original <c>TryResolve</c> race but
/// introduced two new ones:
/// <list type="bullet">
///   <item>The bus has no replay-on-subscribe; subscriptions added after the
///   world's merger started pumping would silently miss the events.</item>
///   <item>The L1 favor verbs and the bus delete events flow on different
///   pumps; the FSM's transient <c>_activeNpcKey</c> state could be primed
///   AFTER the matching <see cref="PlayerInventoryRemoved"/> already
///   arrived, dropping the gift even with both subscriptions attached.</item>
/// </list>
/// The Tier-2 signal service (which #594 / #596 created precisely for this
/// migration) sidesteps both: a single L1 subscription owns the FSM, and
/// the React channel replays the resolved <see cref="GiftAccepted"/>
/// events for late attachers. Consumers (us) only see fully-resolved gift
/// events; nothing is missed.</para>
///
/// <para><b>Replay policy.</b> archetype-B
/// <see cref="ReplayMode.FromSessionStart"/> on both sides. L1 replays
/// favor verbs on cold start so per-NPC <see cref="ArwenFavorState"/>
/// rebuilds. <see cref="IGiftSignalService.Subscribe"/>'s default replay
/// shape atomically replays the resolved gift log to the handler before
/// going live.</para>
///
/// <para><b>Idempotence policy.</b> <em>None</em> from either source's
/// perspective. <see cref="CalibrationService"/> owns per-key dedup at the
/// sink layer via its <c>_observationKeys</c> HashSet keyed
/// <c>SessionId|InstanceId|NpcKey|Item|Delta|Timestamp:O</c>;
/// <see cref="ArwenFavorState.SetExactFavor"/> is an idempotent last-write-wins
/// upsert. A high-water filter on either subscription would be redundant
/// AND slightly wrong — a Mithril restart that re-reads the prior session
/// must let calibration see the replayed gift events so the sink dedup can
/// short-circuit; suppressing them upstream would drop the observations
/// the dedup is designed to collapse.</para>
///
/// <para><b>Threading.</b> The L1 handler runs on the WPF dispatcher via
/// <see cref="DeliveryContext.Marshaled"/>; in test/headless contexts where
/// <see cref="Application.Current"/> is null we fall back to
/// <see cref="DeliveryContext.Inline"/>. The
/// <see cref="IGiftSignalService"/> handler fires synchronously on the
/// service's L1 pump; we marshal onto the WPF dispatcher when one exists
/// so calibration mutations stay on the same thread the L1 favor-snapshot
/// path uses (and the same thread the calibration FSM was always invoked
/// on pre-#608).</para>
/// </summary>
public sealed class FavorIngestionService : BackgroundService
{
    private readonly ILogStreamDriver _driver;
    private readonly FavorLogParser _parser;
    private readonly FavorStateService _state;
    private readonly CalibrationService _calibration;
    private readonly PerCharacterView<ArwenFavorState> _favorView;
    private readonly IActiveCharacterService _activeChar;
    private readonly IGiftSignalService _giftSignal;
    private readonly IDiagnosticsSink? _diag;
    private ILogSubscription? _subscription;
    private IDisposable? _giftSubscription;
    private Dispatcher? _dispatcher;

    public FavorIngestionService(
        ILogStreamDriver driver,
        FavorLogParser parser,
        FavorStateService state,
        CalibrationService calibration,
        PerCharacterView<ArwenFavorState> favorView,
        IActiveCharacterService activeChar,
        SettingsAutoSaver<ArwenSettings> autoSaver,
        IGiftSignalService giftSignal,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _state = state;
        _calibration = calibration;
        _favorView = favorView;
        _activeChar = activeChar;
        _giftSignal = giftSignal;
        _diag = diag;
        _ = autoSaver; // keep alive for PropertyChanged subscription
    }

    /// <summary>
    /// Eager subscription attach per Call 1 / principle eager-always (#695):
    /// both the L1 LocalPlayer pipe and the <see cref="IGiftSignalService"/>
    /// React channel are wired up during the IHostedService chain's
    /// <c>StartAsync</c>, before the trailing world-merger drain starts
    /// (#702 / Call 2 ordering invariant). The Arwen module gate no longer
    /// gates state subscription — gate-driven UI hydration remains
    /// Arwen's own concern (today: none; Arwen tabs hydrate lazily from
    /// the per-character view + L1-driven FavorState).
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Arwen.Ingestion",
            "Subscribing to L1 LocalPlayer pipe (favor snapshot verbs) and "
            + "IGiftSignalService (resolved gift events) for calibration");

        // Resolve UI dispatcher once. Headless/test contexts (no WPF App
        // running) get Inline delivery.
        _dispatcher = Application.Current?.Dispatcher;
        var delivery = _dispatcher is null
            ? DeliveryContext.Inline
            : DeliveryContext.Marshaled(_dispatcher);

        // GiftSignalService.Subscribe replays the full in-session event log
        // to the new handler atomically under its own lock (#585 contract).
        // The handler fires on the signal service's L1 pump thread — we
        // marshal onto the dispatcher so calibration mutations land on the
        // same thread the L1 favor-snapshot path uses.
        _giftSubscription = _giftSignal.Subscribe(OnGiftAccepted);

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
                if (evt is FavorUpdate update)
                {
                    _diag?.Trace("Arwen.Parse", $"FavorUpdate npc={update.NpcKey} favor={update.AbsoluteFavor:F1}");
                    var favor = _favorView.Current;
                    if (favor is not null)
                    {
                        favor.SetExactFavor(update.NpcKey, update.AbsoluteFavor, DateTimeOffset.UtcNow);
                        _favorView.Save();
                        _state.OnFavorUpdated(update.NpcKey);
                    }
                }
                // ProcessDeltaFavor and ProcessDeleteItem are intentionally
                // not consumed here. IGiftSignalService owns the verb-triple
                // correlation on its own single L1 pump; production gifts
                // arrive at calibration via OnGiftAccepted on the React
                // channel.
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = delivery,
                DiagnosticCategory = "Arwen.Ingestion",
                // No SkipProcessedHighWater — CalibrationService owns
                // per-key dedup at the sink layer; the L1 subscription
                // consumes only FavorUpdate (ProcessStartInteraction), so
                // a high-water filter is unnecessary for correctness here.
                // Gift events arrive separately via IGiftSignalService's
                // React channel. See type doc.
            });

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
            _giftSubscription?.Dispose();
            _giftSubscription = null;
        }
    }

    /// <summary>
    /// <see cref="IGiftSignalService"/> handler — fires whenever the signal
    /// service has fully resolved a gift (the verb-triple correlation
    /// produced an item + npc + delta with a known <c>InternalName</c>).
    /// Marshaled onto the WPF dispatcher when one exists so calibration
    /// sink mutations stay on the same thread as the L1 favor-snapshot
    /// path. Headless test contexts run inline.
    /// </summary>
    private void OnGiftAccepted(GiftAccepted gift)
    {
        void Dispatch() => _calibration.OnGiftAccepted(gift);

        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            Dispatch();
        }
        else
        {
            // Async marshal — the L1 favor-snapshot handler is also async-marshaled
            // via the DeliveryContext, so a synchronous Invoke here would risk
            // ordering pile-ups under bursty replay. Per-source order is
            // preserved by the dispatcher's FIFO queue.
            _dispatcher.InvokeAsync(Dispatch);
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        _giftSubscription?.Dispose();
        _giftSubscription = null;
        base.Dispose();
    }
}
