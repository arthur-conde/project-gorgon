using System.Reactive.Subjects;

namespace Mithril.Shared.Telemetry.Export;

/// <summary>
/// Owns the live <see cref="ExporterHealth"/> snapshot consumed by the
/// settings status-line UI. Receives success/failure pulses from the OTel-
/// event-source listener (<see cref="OtlpExporterEventListener"/>) and from
/// health-check probes invoked by the exporter wrapper.
///
/// Exposed as <see cref="IObservable{T}"/> via a <see cref="BehaviorSubject{T}"/>
/// so a late subscriber gets the current snapshot immediately on bind — the
/// settings UI binds via the standard Rx-to-WPF pattern and shouldn't have to
/// wait for the next health pulse to render an initial value.
///
/// <para><strong>Thread safety.</strong> Concurrent <see cref="RecordSuccess"/> /
/// <see cref="RecordFailure"/> calls are safe — both serialise through an
/// internal lock so the read-modify-write of the snapshot is atomic. Required
/// because the success/failure pulses come from at least two sources (the
/// exporter wrapper and the OTel SDK EventSource listener) which run on
/// different threads.</para>
///
/// <para><strong>Subscriber dispatch contract.</strong>
/// <see cref="BehaviorSubject{T}.OnNext"/> fans out to subscribers
/// <em>synchronously</em>, and the fan-out happens while this monitor holds
/// its internal write lock. That means a subscriber's <c>OnNext</c> handler
/// runs on the producer thread — typically the OTel batch-processor thread or
/// the EventSource listener's callback thread — under the lock that serialises
/// future <see cref="RecordSuccess"/> / <see cref="RecordFailure"/> calls.
/// <strong>Subscribers must do their own scheduling</strong> (e.g. Rx
/// <c>ObserveOn</c>, a fire-and-forget WPF <c>Dispatcher.Post</c>) and must
/// not block: a blocking handler stalls the OTel exporter thread and delays
/// batch flushes for subsequent spans. The in-tree consumer
/// (<c>TelemetrySettingsViewModel.OnHealth</c>) already uses
/// <c>SynchronizationContext.Post</c> to fire-and-forget marshal back to
/// the UI thread; future subscribers must follow the same pattern.</para>
/// </summary>
public sealed class ExporterHealthMonitor : IDisposable, IObservable<ExporterHealth>
{
    private readonly BehaviorSubject<ExporterHealth> _subject =
        new(new ExporterHealth(null, null, null));
    private readonly object _writeLock = new();

    public ExporterHealth Current => _subject.Value;

    public void RecordSuccess()
    {
        lock (_writeLock)
        {
            _subject.OnNext(_subject.Value with { LastSuccessUtc = DateTimeOffset.UtcNow, LastError = null });
        }
    }

    public void RecordFailure(string error)
    {
        lock (_writeLock)
        {
            _subject.OnNext(_subject.Value with { LastFailureUtc = DateTimeOffset.UtcNow, LastError = error });
        }
    }

    public IDisposable Subscribe(IObserver<ExporterHealth> observer) => _subject.Subscribe(observer);
    public IDisposable Subscribe(Action<ExporterHealth> onNext) => _subject.Subscribe(onNext);

    public void Dispose() => _subject.Dispose();
}
