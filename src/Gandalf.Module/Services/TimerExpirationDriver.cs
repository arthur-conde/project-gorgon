using Arda.Dispatch;
using Arda.World.Player.Events;
using Microsoft.Extensions.Hosting;
using Mithril.Shared.Diagnostics;

namespace Gandalf.Services;

/// <summary>
/// Drives <see cref="TimerProgressService.CheckExpirations(DateTimeOffset)"/>
/// off Arda's <see cref="CalendarTimeAdvanced"/> domain events
/// (Gandalf scheduler-collapse, #613).
///
/// <para>Replaces the retired <c>TimerExpirationScheduler</c>'s
/// <see cref="System.Windows.Threading.DispatcherTimer"/>-based wake
/// injection. Calendar time is a domain event, not a clock read — the
/// Arda pipeline advances at the source-stream cadence; module schedulers
/// consume the advancement events rather than polling wall-clock time.</para>
///
/// <para><b>Subscribe-in-StartAsync pattern</b> matches the other
/// ingestion services. The Arda pipeline's host startup sequence
/// guarantees this subscription is live before the event drain begins.</para>
/// </summary>
internal sealed class TimerExpirationDriver : BackgroundService
{
    private readonly TimerProgressService _progress;
    private readonly IDomainEventSubscriber _bus;
    private readonly IDiagnosticsSink? _diag;
    private IDisposable? _subscription;

    public TimerExpirationDriver(
        TimerProgressService progress,
        IDomainEventSubscriber bus,
        IDiagnosticsSink? diag = null)
    {
        _progress = progress;
        _bus = bus;
        _diag = diag;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.Subscribe<CalendarTimeAdvanced>(evt =>
        {
            try
            {
                _progress.CheckExpirations(evt.Now);
            }
            catch (Exception ex)
            {
                _diag?.Warn("Gandalf.ExpirationDriver",
                    $"CheckExpirations threw on tick {evt.Metadata.Timestamp:o}: {ex.Message}");
            }
        });

        _diag?.Info("Gandalf.ExpirationDriver",
            "Subscribed to Arda CalendarTimeAdvanced events");

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
        try { _subscription?.Dispose(); }
        finally { _subscription = null; }
        base.Dispose();
    }
}
