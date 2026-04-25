using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Mithril.Shared.Collections;

/// <summary>
/// WPF-binding-friendly wrapper over <see cref="TtlList{T}"/>. Maintains an
/// <see cref="ObservableCollection{T}"/> view that mirrors the live
/// (non-stale) state of the backing list, with mutations marshalled onto
/// a caller-provided dispatch callable so <see cref="INotifyCollectionChanged"/>
/// notifications fire on a single, predictable thread (typically the UI
/// thread).
///
/// We accept an <c>Action&lt;Action&gt; dispatch</c> rather than a
/// <see cref="System.Windows.Threading.Dispatcher"/> directly so the type
/// is testable without an STA thread and so non-WPF consumers (headless
/// tests, future MAUI port) can swap in their own marshalling. WPF
/// production callers wire in <c>a =&gt; Application.Current.Dispatcher.InvokeAsync(a)</c>;
/// tests pass <c>a =&gt; a()</c> for inline dispatch.
///
/// A periodic background timer triggers <see cref="TtlList{T}.DropStale"/>
/// + observable-view reconciliation. The timer fires on a thread-pool
/// thread and immediately marshals back through <c>dispatch</c>, so the
/// observable view is mutated only on the dispatcher thread.
///
/// Dispose to stop the timer and release any retained marshalling state.
/// </summary>
public sealed class TtlObservableCollection<T> : INotifyCollectionChanged, IDisposable
{
    private readonly TtlList<T> _backing;
    private readonly ObservableCollection<T> _observable = new();
    private readonly Action<Action> _dispatch;
    private readonly Timer _evictionTimer;
    private readonly object _disposeGate = new();
    private bool _disposed;

    public TtlObservableCollection(
        TimeSpan ttl,
        Action<Action> dispatch,
        TimeSpan? evictionInterval = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        _backing = new TtlList<T>(ttl, time);
        _dispatch = dispatch;
        var interval = evictionInterval ?? TimeSpan.FromMinutes(1);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(evictionInterval), "Eviction interval must be positive.");
        _evictionTimer = new Timer(_ => Reconcile(), state: null, interval, interval);
    }

    /// <summary>
    /// Read-only observable view, suitable as a XAML <c>ItemsSource</c>.
    /// Mutates only on the dispatcher thread.
    /// </summary>
    public IReadOnlyList<T> View => _observable;

    public event NotifyCollectionChangedEventHandler? CollectionChanged
    {
        add => _observable.CollectionChanged += value;
        remove => _observable.CollectionChanged -= value;
    }

    /// <summary>
    /// Append <paramref name="value"/>. Dispatched onto the dispatcher
    /// thread for the observable mutation; the backing list is updated
    /// immediately on the calling thread (since <see cref="TtlList{T}"/>
    /// is itself thread-safe).
    /// </summary>
    public void Add(T value)
    {
        if (_disposed) return;
        _backing.Add(value);
        _dispatch(() =>
        {
            if (_disposed) return;
            _observable.Add(value);
        });
    }

    /// <summary>
    /// Remove every entry whose value satisfies <paramref name="match"/>.
    /// Returns the count removed from the backing list (the observable
    /// view is reconciled asynchronously on the dispatcher thread).
    /// </summary>
    public int Remove(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (_disposed) return 0;
        var removed = _backing.Remove(match);
        if (removed > 0)
        {
            _dispatch(() =>
            {
                if (_disposed) return;
                for (var i = _observable.Count - 1; i >= 0; i--)
                    if (match(_observable[i])) _observable.RemoveAt(i);
            });
        }
        return removed;
    }

    /// <summary>
    /// Force a reconciliation pass: drop stale entries from the backing
    /// list and mirror the resulting state into the observable view.
    /// Public so tests and consumers can drain on demand without waiting
    /// for the next timer tick.
    /// </summary>
    public void Reconcile()
    {
        if (_disposed) return;
        _dispatch(() =>
        {
            if (_disposed) return;
            var alive = new HashSet<T>(_backing.Snapshot());
            for (var i = _observable.Count - 1; i >= 0; i--)
                if (!alive.Contains(_observable[i])) _observable.RemoveAt(i);
        });
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _evictionTimer.Dispose();
    }
}
