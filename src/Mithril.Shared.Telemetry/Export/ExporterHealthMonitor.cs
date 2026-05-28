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
/// Thread safety: <see cref="BehaviorSubject{T}"/> serialises OnNext through
/// its internal lock, so concurrent <see cref="RecordSuccess"/> /
/// <see cref="RecordFailure"/> calls from different threads are safe; the
/// observable snapshot is the last value written.
/// </summary>
public sealed class ExporterHealthMonitor : IDisposable, IObservable<ExporterHealth>
{
    private readonly BehaviorSubject<ExporterHealth> _subject =
        new(new ExporterHealth(null, null, null));

    public ExporterHealth Current => _subject.Value;

    public void RecordSuccess() =>
        _subject.OnNext(Current with { LastSuccessUtc = DateTimeOffset.UtcNow, LastError = null });

    public void RecordFailure(string error) =>
        _subject.OnNext(Current with { LastFailureUtc = DateTimeOffset.UtcNow, LastError = error });

    public IDisposable Subscribe(IObserver<ExporterHealth> observer) => _subject.Subscribe(observer);
    public IDisposable Subscribe(Action<ExporterHealth> onNext) => _subject.Subscribe(onNext);

    public void Dispose() => _subject.Dispose();
}
