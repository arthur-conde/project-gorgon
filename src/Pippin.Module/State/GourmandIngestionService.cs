using System.Windows;
using Mithril.Shared.Character;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Microsoft.Extensions.Hosting;
using Pippin.Domain;
using Pippin.Parsing;

namespace Pippin.State;

/// <summary>
/// Subscribes to the L1 (#550) driver's LocalPlayer pipe and feeds parsed
/// <see cref="FoodsConsumedReport"/> events into <see cref="GourmandStateMachine"/>.
///
/// <para><b>L1 migration disposition (#549 row, #550 PR 3 archetype-B).</b>
/// Pippin is the canonical demo of the
/// <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/> idempotence
/// pattern: <see cref="GourmandStateMachine.HandleReport"/> does
/// <c>Clear()</c>+repopulate (in-session idempotent), but every cold start
/// re-runs the full snapshot-overwrite apply against every replayed report.
/// The high-water filter persisted in <see cref="GourmandState.LastProcessedSequence"/>
/// drops envelopes whose <c>Sequence</c> the prior session already processed,
/// stabilising the restart shape. Pre-L1 Pippin had no idempotence at all
/// — the high-water introduces restart-stability where none existed, it does
/// NOT fix a <c>UtcNow</c> double-count bug (the umbrella's G1 framing was
/// factually wrong against live code; verified in #549 G1 correction).</para>
///
/// <para><b>Subscription shape.</b>
/// <list type="bullet">
///   <item><see cref="ReplayMode.FromSessionStart"/> — Pippin must catch the
///   most-recent in-session <c>FoodsConsumedReport</c> after a Mithril restart
///   to repopulate the in-memory state.</item>
///   <item><see cref="DeliveryContext.Marshaled"/> on the UI dispatcher — the
///   handler raises <see cref="GourmandStateMachine.StateChanged"/>, consumed
///   by <c>GourmandViewModel</c>'s bound
///   <c>ObservableCollection&lt;FoodItemViewModel&gt;</c>. The driver routes
///   each envelope through <see cref="Dispatcher"/> before invoking the
///   handler, retiring the per-service hand-rolled
///   <c>Application.Current?.Dispatcher; CheckAccess; InvokeAsync</c> helper
///   (#550 capability E).</item>
///   <item><see cref="LogSubscriptionOptions.SkipProcessedHighWater"/> seeded
///   from <see cref="GourmandState.LastProcessedSequence"/> — restart-stable
///   idempotence (#550 capability F). The high-water is advanced after each
///   delivered envelope (whether it produced an event or not) and persisted
///   alongside the rest of <see cref="GourmandState"/> via the existing
///   <see cref="GourmandStateService"/> debounce-write path.</item>
///   <item>Containment is owned by the driver: the per-service
///   <c>ThrottledWarn</c> + try/catch retired (#550 capability C). Failures
///   surface on <c>IDiagnosticsSink</c> under <c>Pippin.Ingestion</c> via the
///   driver's <see cref="LogSubscriptionOptions.DiagnosticCategory"/>
///   override; a stuck handler trips
///   <see cref="LogSubscriptionState.Degraded"/> and surfaces on
///   <c>IAttentionAggregator</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class GourmandIngestionService : BackgroundService
{
    private readonly ILogStreamDriver _driver;
    private readonly GourmandLogParser _parser;
    private readonly GourmandStateMachine _state;
    private readonly GourmandStateService _stateService;
    private readonly PerCharacterView<GourmandState> _view;
    private readonly IDiagnosticsSink? _diag;
    private ILogSubscription? _subscription;

    public GourmandIngestionService(
        ILogStreamDriver driver,
        GourmandLogParser parser,
        GourmandStateMachine state,
        GourmandStateService stateService,
        PerCharacterView<GourmandState> view,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _state = state;
        _stateService = stateService;
        _view = view;
        _diag = diag;
    }

    /// <summary>
    /// Eager subscription attach per Call 1 / principle eager-always (#695):
    /// the active-character wait, persisted-state load, and L1 subscription
    /// all happen during the IHostedService chain's <c>StartAsync</c>,
    /// before the trailing world-merger drain starts (#702 / Call 2).
    /// Pippin's module gate no longer gates state subscription.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _diag?.Info("Pippin", "Waiting for active character…");
        await WaitForActiveCharacterAsync(cancellationToken).ConfigureAwait(false);

        try { await _stateService.LoadAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _diag?.Warn("Pippin", $"Failed to load state: {ex.Message}"); }

        _diag?.Info("Pippin", "State hydrated — subscribing to L1 driver (LocalPlayer pipe) eagerly");

        // Seed the high-water from the freshly hydrated per-character state.
        // null = no prior session processed (first run / pre-#550 fresh state).
        // The driver drops every envelope whose Sequence is <= this value
        // before handler invocation — restart-safe idempotence (#549 / #550
        // capability F). Pippin is the canonical demo of the pattern.
        var persistedHighWater = _view.Current?.LastProcessedSequence;

        // Headless / no-Application contexts fall back to Inline delivery.
        var dispatcher = Application.Current?.Dispatcher;
        var deliveryContext = dispatcher is null
            ? DeliveryContext.Inline
            : DeliveryContext.Marshaled(dispatcher);

        // L0.5 (#532) eats the [ts] + LocalPlayer: envelope; the parser now
        // consumes LocalPlayerLogLine.Data directly (anchor dropped per the
        // #550 PR #555 review).
        _subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var payload = envelope.Payload;
                var evt = _parser.TryParse(payload.Data, payload.Timestamp.UtcDateTime);
                if (evt is GourmandEvent ge)
                {
                    _diag?.Trace(
                        "Pippin.Parse",
                        $"FoodsConsumedReport with {(ge as FoodsConsumedReport)?.Foods.Count ?? 0} entries");
                    _state.Apply(ge);
                }
                // Advance the high-water on every delivered envelope. The
                // driver hands them up in monotonic Sequence order from the
                // L0.5 pipe's replay snapshot then live tail, so this is the
                // canonical "I have processed every envelope up to seq X"
                // mark. The existing GourmandStateService.OnChanged →
                // MarkDirty → debounce → _view.Save() path persists it (the
                // state-machine StateChanged event already runs the debounce
                // when an event was applied; on no-event lines we touch the
                // view directly so the next StateChanged-driven flush
                // captures the advance, and a one-shot flush on disposal
                // catches the tail).
                var current = _view.Current;
                if (current is not null)
                {
                    current.LastProcessedSequence = payload.Sequence;
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = deliveryContext,
                SkipProcessedHighWater = persistedHighWater,
                DiagnosticCategory = "Pippin.Ingestion",
            });

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Park until the host stops. The L1 subscription runs its own pump
        // on a Task.Run; ExecuteAsync's job is to dispose it on shutdown.
        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
            // Best-effort flush of the trailing high-water advance so a
            // crash-after-no-event-applied run doesn't lose progress. The
            // GourmandStateService debounce writes on StateChanged; a tail
            // of replay-skip + no-events leaves the latest high-water
            // un-flushed unless we nudge a save here.
            try { _view.Save(); } catch { /* best-effort on host stop */ }
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private async Task WaitForActiveCharacterAsync(CancellationToken ct)
    {
        if (_view.Current is not null) return;
        var tcs = new TaskCompletionSource();
        void Handler(object? _, EventArgs __)
        {
            if (_view.Current is not null) tcs.TrySetResult();
        }
        _view.CurrentChanged += Handler;
        try
        {
            if (_view.Current is not null) return;
            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _view.CurrentChanged -= Handler;
        }
    }
}
