using System.ComponentModel;

namespace Mithril.Shared.Modules;

/// <summary>
/// Default <see cref="IAttentionAggregator"/> implementation. Subscribes to
/// every supplied source's <see cref="IAttentionSource.Changed"/> event,
/// marshals through the caller-supplied dispatch callable, then recomputes
/// <see cref="TotalCount"/> / <see cref="Entries"/> and raises notifications.
///
/// We accept an <c>Action&lt;Action&gt; dispatch</c> rather than a
/// <see cref="System.Windows.Threading.Dispatcher"/> directly so the type is
/// testable without an STA thread (see <see cref="Mithril.Shared.Collections.TtlObservableCollection{T}"/>
/// for the same pattern). WPF production callers wire in
/// <c>a =&gt; Application.Current.Dispatcher.InvokeAsync(a)</c>; tests pass
/// <c>a =&gt; a()</c> for inline dispatch.
/// </summary>
public sealed class AttentionAggregator : IAttentionAggregator, IDisposable
{
    private readonly IReadOnlyList<IAttentionSource> _sources;
    private readonly Action<Action> _dispatch;
    private readonly Dictionary<IAttentionSource, EventHandler> _handlers = new();
    private readonly object _gate = new();
    private List<AttentionEntry> _entries;
    private int _totalCount;
    private bool _disposed;

    public AttentionAggregator(IEnumerable<IAttentionSource> sources, Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToList();
        _dispatch = dispatch ?? (a => a());
        _entries = SnapshotEntries();
        _totalCount = _entries.Sum(e => e.Count);

        foreach (var source in _sources)
        {
            EventHandler handler = (_, _) => OnSourceChanged(source);
            _handlers[source] = handler;
            source.Changed += handler;
        }
    }

    public int TotalCount => _totalCount;
    public bool HasAttention => _totalCount > 0;
    public IReadOnlyList<AttentionEntry> Entries => _entries;

    public int CountFor(string moduleId)
    {
        var snapshot = _entries;
        for (var i = 0; i < snapshot.Count; i++)
            if (snapshot[i].ModuleId == moduleId) return snapshot[i].Count;
        return 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<AttentionChangedEventArgs>? AttentionChanged;

    private void OnSourceChanged(IAttentionSource source)
    {
        if (_disposed) return;
        _dispatch(() =>
        {
            if (_disposed) return;
            int newCount;
            int newTotal;
            bool hadAttention;
            bool hasAttention;
            List<AttentionEntry> newEntries;
            lock (_gate)
            {
                newCount = source.Count;
                newEntries = SnapshotEntries();
                newTotal = newEntries.Sum(e => e.Count);
                hadAttention = _totalCount > 0;
                hasAttention = newTotal > 0;
                _entries = newEntries;
                _totalCount = newTotal;
            }

            AttentionChanged?.Invoke(this, new AttentionChangedEventArgs(source.ModuleId, newCount));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Entries)));
            if (hadAttention != hasAttention)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAttention)));
        });
    }

    private List<AttentionEntry> SnapshotEntries()
    {
        var list = new List<AttentionEntry>(_sources.Count);
        foreach (var s in _sources)
            list.Add(new AttentionEntry(s.ModuleId, s.DisplayLabel, s.Count));
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (source, handler) in _handlers)
            source.Changed -= handler;
        _handlers.Clear();
    }
}
