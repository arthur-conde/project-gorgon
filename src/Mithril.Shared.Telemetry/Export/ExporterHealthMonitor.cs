using System.Reactive.Subjects;

namespace Mithril.Shared.Telemetry.Export;

/// <summary>
/// Owns the live <see cref="ExporterHealth"/> snapshot consumed by the
/// settings status-line UI. Receives success/failure pulses from the
/// OTel-event-source listener (a future enhancement — see remarks) and from
/// health-check probes invoked by the exporter wrapper.
///
/// Exposed as <see cref="IObservable{T}"/> via a <see cref="BehaviorSubject{T}"/>
/// so a late subscriber gets the current snapshot immediately on bind — the
/// settings UI binds via the standard Rx-to-WPF pattern and shouldn't have to
/// wait for the next health pulse to render an initial value.
///
/// Thread safety: concurrent <see cref="RecordSuccess"/> / <see cref="RecordFailure"/>
/// calls are safe — both serialise through an internal lock so the read-modify-write
/// of the snapshot is atomic. Required because the success/failure pulses come from
/// at least two sources (the exporter wrapper and the OTel SDK EventSource listener)
/// which run on different threads.
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
