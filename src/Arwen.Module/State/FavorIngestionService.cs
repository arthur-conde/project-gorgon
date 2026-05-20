using System.Windows;
using System.Windows.Threading;
using Arwen.Domain;
using Arwen.Parsing;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Microsoft.Extensions.Hosting;

namespace Arwen.State;

/// <summary>
/// Eager Arwen ingestion that maintains the canonical per-character exact-favor
/// snapshots (<see cref="ArwenFavorState"/>) and feeds <see cref="CalibrationService"/>
/// from the L1 (#550) driver's LocalPlayer pipe.
///
/// <para><b>Replay policy.</b> archetype-B
/// <see cref="ReplayMode.FromSessionStart"/> — rebuilds the exact-favor
/// snapshots and gift-calibration observations on every cold start by draining
/// the whole session backlog. Without replay the post-restart
/// <see cref="ArwenFavorState"/> would be missing every favor change that
/// happened before Mithril attached, and <see cref="CalibrationService"/>
/// wouldn't see the in-session <c>DeleteItem</c>/<c>DeltaFavor</c> pair that
/// derives a gift's per-unit favor rate.</para>
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
/// <para><b>Threading.</b> Handler runs on the WPF dispatcher via
/// <see cref="DeliveryContext.Marshaled"/> — <see cref="ArwenFavorState.Favor"/>
/// is bound to UI, <c>_favorView.Save()</c> persists via per-character JSON,
/// and <see cref="FavorStateService.OnFavorUpdated"/> raises
/// <c>StateChanged</c>/<c>FavorChanged</c> consumed by bound view-models.
/// The pre-L1 hand-rolled <c>Dispatch()</c> helper (Application.Current
/// dispatcher peek + CheckAccess + InvokeAsync) is retired (#550 capability E).
/// In test/headless contexts where <see cref="Application.Current"/> is null
/// we fall back to <see cref="DeliveryContext.Inline"/>.</para>
///
/// <para><b>Containment.</b> The L1 driver wraps each handler invocation in
/// try/catch + rate-limited Warn under the <c>Arwen.Ingestion</c> diag
/// category. The pre-L1 per-service <see cref="ThrottledWarn"/> field, ctor
/// init, and try/catch around the switch are retired (#550 capability C).</para>
/// </summary>
public sealed class FavorIngestionService : BackgroundService
{
    private readonly ILogStreamDriver _driver;
    private readonly FavorLogParser _parser;
    private readonly FavorStateService _state;
    private readonly CalibrationService _calibration;
    private readonly PerCharacterView<ArwenFavorState> _favorView;
    private readonly IActiveCharacterService _activeChar;
    private readonly IDiagnosticsSink? _diag;
    private readonly ModuleGate _gate;
    private ILogSubscription? _subscription;

    public FavorIngestionService(
        ILogStreamDriver driver,
        FavorLogParser parser,
        FavorStateService state,
        CalibrationService calibration,
        PerCharacterView<ArwenFavorState> favorView,
        IActiveCharacterService activeChar,
        SettingsAutoSaver<ArwenSettings> autoSaver,
        ModuleGates gates,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _state = state;
        _calibration = calibration;
        _favorView = favorView;
        _activeChar = activeChar;
        _diag = diag;
        _ = autoSaver; // keep alive for PropertyChanged subscription
        _gate = gates.For("arwen");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Arwen.Ingestion", "Waiting for module gate…");
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
        _diag?.Info("Arwen.Ingestion", "Gate opened — subscribing to L1 driver (LocalPlayer pipe) for favor events");

        // Resolve UI dispatcher once. Headless/test contexts (no WPF App
        // running) get Inline delivery — the production path always has a
        // dispatcher because Arwen is Eager and the shell wires App before
        // any module gate opens.
        var dispatcher = Application.Current?.Dispatcher;
        var delivery = dispatcher is null
            ? DeliveryContext.Inline
            : DeliveryContext.Marshaled(dispatcher);

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

                    case ItemDeleted deleted:
                        _calibration.OnItemDeleted(deleted.InstanceId, ts);
                        break;

                    case FavorDelta delta:
                        _diag?.Trace("Arwen.Parse", $"FavorDelta npc={delta.NpcKey} delta={delta.Delta:F1}");
                        _calibration.OnDeltaFavor(delta.NpcKey, delta.Delta, ts);
                        break;
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
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }
}
