namespace Mithril.Shared.Collections;

/// <summary>
/// Ordered, thread-safe collection whose entries auto-evict after a
/// time-to-live. Eviction is lazy: stale entries are dropped on every
/// public access, with no background timer. Items are stored in
/// insertion order — which equals chronological order because each entry
/// captures its enqueue time from the injected <see cref="TimeProvider"/>
/// (so callers cannot accidentally hand non-monotonic timestamps).
///
/// Designed for small N (under ~100 entries in practice). All operations
/// are O(N): the backing store is a <see cref="List{T}"/> and removals
/// from the front shift the tail. If a future use site needs O(1) FIFO
/// at much larger N, a sibling <c>TtlQueue&lt;T&gt;</c> can be added
/// without touching this type.
///
/// Thread safety: every public member acquires an internal lock, so
/// concurrent callers from multiple threads are safe. Readers see a
/// consistent snapshot bounded by the lock.
/// </summary>
public sealed class TtlList<T>
{
    private readonly object _gate = new();
    private readonly List<(T Value, DateTime EnqueuedAt)> _items = new();
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;

    public TtlList(TimeSpan ttl, TimeProvider? time = null)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        _ttl = ttl;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Total number of stored entries, including ones that are stale but
    /// not yet evicted. Reflects post-eviction count once any access has
    /// triggered <see cref="DropStale"/>.
    /// </summary>
    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>
    /// Append an entry. Captures the enqueue time from the injected
    /// <see cref="TimeProvider"/> at call time.
    /// </summary>
    public void Add(T value)
    {
        lock (_gate) _items.Add((value, _time.GetUtcNow().UtcDateTime));
    }

    /// <summary>
    /// Pop the oldest non-stale entry, evicting any stale entries
    /// encountered along the way. Returns false if the collection is
    /// empty (or empty after eviction).
    /// </summary>
    public bool TryRemoveOldest(out T value)
    {
        lock (_gate)
        {
            DropStaleLocked();
            if (_items.Count == 0)
            {
                value = default!;
                return false;
            }
            value = _items[0].Value;
            _items.RemoveAt(0);
            return true;
        }
    }

    /// <summary>
    /// Remove every entry whose value satisfies <paramref name="match"/>.
    /// Returns the number removed. Does not consider staleness — a caller
    /// who explicitly removes by predicate gets exactly what they ask for.
    /// </summary>
    public int Remove(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        lock (_gate)
        {
            return _items.RemoveAll(e => match(e.Value));
        }
    }

    /// <summary>
    /// Snapshot copy of all currently-live (non-stale) entries in
    /// insertion order. Mutations to the returned list do not affect the
    /// backing store. Triggers eviction as a side effect.
    /// </summary>
    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate)
        {
            DropStaleLocked();
            var copy = new List<T>(_items.Count);
            foreach (var (v, _) in _items) copy.Add(v);
            return copy;
        }
    }

    /// <summary>
    /// Explicit eviction pass. Useful as a piggyback drain for callers
    /// that hold many <see cref="TtlList{T}"/> instances and want to
    /// purge them all on a single event tick.
    /// </summary>
    public void DropStale()
    {
        lock (_gate) DropStaleLocked();
    }

    /// <summary>MUST be called with <see cref="_gate"/> held.</summary>
    private void DropStaleLocked()
    {
        if (_items.Count == 0) return;
        var now = _time.GetUtcNow().UtcDateTime;
        var firstAlive = _items.FindIndex(e => now - e.EnqueuedAt <= _ttl);
        if (firstAlive < 0) _items.Clear();
        else if (firstAlive > 0) _items.RemoveRange(0, firstAlive);
    }
}
