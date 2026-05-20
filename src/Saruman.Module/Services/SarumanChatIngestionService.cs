using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;
using Saruman.Domain;
using Saruman.Parsing;

namespace Saruman.Services;

/// <summary>
/// Migrated to the L1 driver (#550 PR 3, archetype-B). Subscribes to the
/// <see cref="IChatLogStream"/> path via
/// <see cref="ILogStreamDriver.Subscribe{T}"/> as a typed
/// <see cref="LogEnvelope{T}"/> stream of <see cref="RawLogLine"/>, applies
/// the chat parser, and marks the matched code <see cref="WordOfPowerState.Spent"/>
/// on the codebook.
///
/// <para><b>Replay policy.</b> <see cref="ReplayMode.LiveOnly"/> — chat is
/// structurally live-only by construction (<c>ChatLogStream.cs</c> seeds the
/// per-day directory to current-end before the first emission, see #549
/// Divergence 1). The L1 driver also coerces any non-<c>LiveOnly</c> chat
/// subscription to <c>LiveOnly</c> with a one-time Info diagnostic;
/// passing <c>LiveOnly</c> here makes the intent explicit at the call site
/// rather than relying on the coercion.</para>
///
/// <para><b>Delivery context.</b> <see cref="DeliveryContext.Inline"/> — the
/// handler only flips <c>SarumanCodebookService.MarkSpent</c> state and
/// raises <c>CodebookChanged</c>; the VM owns its own dispatcher hop
/// (<c>SarumanViewModel.Dispatch(Refresh)</c>) on the changed event, so the
/// ingestion path does not touch bound collections.</para>
///
/// <para><b>Idempotence.</b> None needed (#549 audit row). <c>MarkSpent</c>
/// is a state-flip that no-ops if already <see cref="WordOfPowerState.Spent"/>,
/// and the chat stream never replays anyway.</para>
///
/// <para><b>Containment retired.</b> The pre-L1 <c>ThrottledWarn</c> + per-
/// line try/catch are gone — the L1 driver wraps every handler invocation
/// in try/catch + rate-limited Warn under the
/// <see cref="LogSubscriptionOptions.DiagnosticCategory"/> bucket
/// ("Saruman.Ingestion"), retiring the bespoke instance per #550 capability C.</para>
/// </summary>
public sealed class SarumanChatIngestionService : BackgroundService
{
    private const string DiagCategory = "Saruman.Ingestion";

    private readonly ILogStreamDriver _driver;
    private readonly WordOfPowerChatParser _parser;
    private readonly SarumanCodebookService _codebook;
    private readonly ModuleGate _gate;
    private ILogSubscription? _subscription;

    public SarumanChatIngestionService(
        ILogStreamDriver driver,
        WordOfPowerChatParser parser,
        SarumanCodebookService codebook,
        ModuleGates gates)
    {
        _driver = driver;
        _parser = parser;
        _codebook = codebook;
        _gate = gates.For("saruman");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);

        _subscription = _driver.Subscribe<RawLogLine>(
            envelope =>
            {
                if (_parser.TryParse(envelope.Payload.Line, envelope.Payload.Timestamp.UtcDateTime) is WordOfPowerSpoken s)
                    _codebook.MarkSpent(s.Code, s.Timestamp);
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                // Explicit LiveOnly — chat is structurally live-only, but
                // expressing intent at the call site is the #549 disposition.
                // The driver auto-coerces non-LiveOnly to LiveOnly with one
                // Info diag; passing LiveOnly here avoids that diagnostic.
                ReplayMode = ReplayMode.LiveOnly,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = DiagCategory,
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
