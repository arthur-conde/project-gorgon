using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;
using Saruman.Domain;
using Saruman.Parsing;
using Saruman.Settings;

namespace Saruman.Services;

/// <summary>
/// Post-#550 PR 3 L1 migration. Subscribes to the L1 driver's LocalPlayer
/// pipe (the typed L0.5 actor-classified surface) with archetype-B options
/// per #549's disposition row for this consumer:
///
/// <list type="bullet">
///   <item><c>ReplayMode.SinceSubscribe</c> — lazy module; only live
///   discoveries matter for codebook authority. The driver drops replay-phase
///   envelopes structurally so a gate-open after the first discovery doesn't
///   re-inflate <see cref="KnownWord.DiscoveryCount"/> by walking the backlog
///   again.</item>
///   <item><c>DeliveryContext.Inline</c> — the VM self-dispatches on
///   <c>CodebookChanged</c>; no need for the driver to marshal onto a UI
///   dispatcher.</item>
///   <item><c>SkipProcessedHighWater</c> — per-code dedup is impossible
///   because <c>DiscoveryCount</c> is monotonic, so we persist the highest
///   <see cref="LocalPlayerLogLine.Sequence"/> we've ever applied (in
///   <see cref="SarumanState.DiscoveryHighWaterSequence"/>, per-character)
///   and reuse it on the next subscription. Restart-safe defence-in-depth
///   even with <c>SinceSubscribe</c>: future replay-window variants of
///   <c>SinceSubscribe</c> (see <see cref="ReplayMode.SinceSubscribe"/>
///   docs) would still be filtered correctly without a code change here.</item>
/// </list>
///
/// <para><b>Containment retired.</b> The driver wraps every handler
/// invocation in try/catch + rate-limited Warn (#550 capability C), so the
/// per-service <c>ThrottledWarn</c> ctor + try/catch this file used to hold
/// are gone. Failures surface on <c>IDiagnosticsSink</c> under the
/// <c>Saruman.Ingestion</c> category via the driver's
/// <see cref="LogSubscriptionOptions.DiagnosticCategory"/> override (same
/// category the pre-L1 hand-rolled <c>ThrottledWarn</c> used, so log
/// consumers see no category churn).</para>
///
/// <para><b>Character switching.</b> The <see cref="SarumanCodebookService"/>
/// reads / writes through <see cref="PerCharacterView{T}"/>, so a switch
/// silently swaps both the codebook and its high-water atomically. The
/// driver subscription is created once at gate-open and stays bound to the
/// current Mithril process — character-switch correctness lives at the
/// codebook layer (mutations no-op when no character is active), not the
/// subscription layer.</para>
/// </summary>
public sealed class SarumanDiscoveryIngestionService : BackgroundService
{
    private readonly ILogStreamDriver _driver;
    private readonly WordOfPowerDiscoveredParser _parser;
    private readonly SarumanCodebookService _codebook;
    private readonly ModuleGate _gate;
    private readonly IDiagnosticsSink? _diag;
    private ILogSubscription? _subscription;

    public SarumanDiscoveryIngestionService(
        ILogStreamDriver driver,
        WordOfPowerDiscoveredParser parser,
        SarumanCodebookService codebook,
        ModuleGates gates,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _codebook = codebook;
        _gate = gates.For("saruman");
        _diag = diag;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);

        // Persisted per-character high-water sequence — replay envelopes
        // (and any live envelope at or below this seq, e.g. a restart-window
        // duplicate) are dropped by the driver before reaching the handler.
        // Null is the documented "no filter" signal (see
        // LogSubscriptionOptions.SkipProcessedHighWater).
        var highWater = _codebook.DiscoveryHighWaterSequence;

        _diag?.Info(
            "Saruman.Ingestion",
            $"Subscribing to L1 driver (LocalPlayer pipe) for word-of-power discoveries (highWater={highWater?.ToString() ?? "none"}).");

        _subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var payload = envelope.Payload;
                if (_parser.TryParse(payload.Data, payload.Timestamp.UtcDateTime) is WordOfPowerDiscovered d)
                {
                    _codebook.RecordDiscovery(d, payload.Sequence);
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.SinceSubscribe,
                DeliveryContext = DeliveryContext.Inline,
                SkipProcessedHighWater = highWater,
                DiagnosticCategory = "Saruman.Ingestion",
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
