using Microsoft.Extensions.Hosting;
using Mithril.Shared.Diagnostics;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;

namespace Gandalf.Services;

/// <summary>
/// Drives <see cref="TimerProgressService.CheckExpirations(DateTimeOffset)"/>
/// off PlayerWorld's <see cref="CalendarTimeAdvanced"/> domain events
/// (Gandalf scheduler-collapse, #613, world-sim migration item #12).
///
/// <para>Replaces the retired <c>TimerExpirationScheduler</c>'s
/// <see cref="System.Windows.Threading.DispatcherTimer"/>-based wake
/// injection. Per design notebook principle 13 — "calendar time is a domain
/// event, not a clock read" — the world clock advances at the source-stream
/// cadence; module schedulers consume the advancement events rather than
/// polling a real-wall-clock <see cref="DateTimeOffset"/>. During active play
/// PG emits enough log noise to keep <c>CalendarTimeAdvanced</c> within one
/// real second of wall-clock; idle stretches are exactly the cases the
/// retired scheduler covered with its "wake at the next expiration" timer,
/// and those expirations now fire on the next tick after the player resumes
/// activity (drain-time alarms gate on Mode at the audio boundary so they
/// silently catch up rather than ringing for every missed cycle).</para>
///
/// <para><b>Subscribe-in-StartAsync pattern</b> matches the six PR #705
/// ingestion services that retired their <c>ModuleGate.WaitAsync</c> waits.
/// The <c>WorldMergerStartHostedService</c> trailing-registration invariant
/// (PR #702 / Call 2) guarantees this subscription is live before the
/// merger drain begins — so no <c>CalendarTimeAdvanced</c> event slips
/// past the driver during cold-start.</para>
/// </summary>
internal sealed class TimerExpirationDriver : BackgroundService
{
    private readonly TimerProgressService _progress;
    private readonly IPlayerWorld _world;
    private readonly IDiagnosticsSink? _diag;
    private IDisposable? _subscription;

    public TimerExpirationDriver(
        TimerProgressService progress,
        IPlayerWorld world,
        IDiagnosticsSink? diag = null)
    {
        _progress = progress;
        _world = world;
        _diag = diag;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Synchronous subscribe before base.StartAsync — matches the Call 1 /
        // #705 ingestion-service shape. The trailing-registered merger drain
        // (#702 / Call 2) starts only after every hosted service's StartAsync
        // has completed, so the handler is guaranteed live before the first
        // CalendarTimeAdvanced frame dispatches.
        _subscription = _world.Bus.Subscribe<CalendarTimeAdvanced>(frame =>
        {
            // Pass the event's Now — not a real-wall-clock read — so the
            // expiration check is replay-deterministic. CheckExpirations is
            // mode-agnostic: it advances state (stamps CompletedAt / re-arms
            // recurring rows) regardless of Mode. The audio gate lives
            // downstream in TimerAlarmService.OnTimerReady.
            try
            {
                _progress.CheckExpirations(frame.Payload.Now);
            }
            catch (Exception ex)
            {
                // Per principle 11 — bus delivery happens inside the world's
                // frame-resolution loop and any subscriber exception would
                // poison the merger thread. Swallow + diagnose; expiry will
                // fire again on the next tick.
                _diag?.Warn("Gandalf.ExpirationDriver",
                    $"CheckExpirations threw on tick {frame.Timestamp:o}: {ex.Message}");
            }
        });

        _diag?.Info("Gandalf.ExpirationDriver",
            "Subscribed to PlayerWorld CalendarTimeAdvanced (scheduler-collapse, #613)");

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected on host stop */ }
    }

    public override void Dispose()
    {
        // The BackgroundService base may finish ExecuteAsync's finally before
        // Dispose under host shutdown, but a quick StartAsync → StopAsync →
        // Dispose triplet (or a test that never starts ExecuteAsync) can
        // leak the subscription. Dispose it idempotently here as well.
        try { _subscription?.Dispose(); }
        finally { _subscription = null; }
        base.Dispose();
    }
}
