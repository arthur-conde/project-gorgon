using System.Diagnostics.CodeAnalysis;

namespace Mithril.Shared.Correlation;

/// <summary>
/// Tier-1 keyed-correlation primitive — see
/// <c>docs/cross-source-correlation.md</c> for the full tier hierarchy this
/// sits inside (#523 / #511 design notes). A thin multi-map of
/// <typeparamref name="TKey"/> → FIFO list of <typeparamref name="TReq"/>
/// entries, each timestamped at enqueue time. Entries are evicted by TTL —
/// lazily on <see cref="TryTake"/> (stale entries at the head of a bucket are
/// evicted along the way) and eagerly via <see cref="DrainStale"/>. Neither
/// <see cref="Add"/> nor <see cref="Count"/> triggers eviction, so consumers
/// that only enqueue without taking must call <see cref="DrainStale"/>
/// periodically to bound bucket growth. Every evicted entry is routed through
/// an optional unmatched callback so the consumer's "what to do when
/// correlation fails" policy is explicit rather than a silent drop.
///
/// Intended use is the cross-source half-correlation pattern that
/// <c>InventoryService</c> already implements by hand: one stream carries a
/// keyed request (e.g. Player.log <c>ProcessAddItem(InternalName(id), …)</c>),
/// the other carries a keyed response (chat <c>[Status] X xN added</c>), they
/// can arrive in either order within an arrival window, and neither pre-knows
/// the other's payload. Each side gets its own correlator; the side that
/// arrives first <see cref="Add"/>s, the side that arrives second
/// <see cref="TryTake"/>s.
///
/// The TTL is a *correlation gate*, not an ordering oracle — entries that age
/// out are surfaced via the unmatched callback so the consumer can decide what
/// the absence means (silent drop, "credit 1", diagnostic warning, ...). This
/// replaces the prior "credit at least 1 if the add never arrived" guess in
/// Legolas's <c>_pendingAdds</c> with a deliberate policy.
///
/// Thread safety: every public member acquires an internal lock. The unmatched
/// callback is invoked *outside* the lock so it may safely call back into the
/// correlator (or take other application locks) without deadlocking. Each
/// eviction sweep collects evicted entries into a local list and fires the
/// callback after releasing the gate; if a callback throws, the remaining
/// callbacks for the same sweep still run, and any exceptions are aggregated
/// into a single <see cref="AggregateException"/> thrown after the sweep
/// completes. The "explicit policy" guarantee — every evicted entry receives
/// its callback — holds even when individual callbacks fault.
///
/// Performance: designed for small N (handful of distinct keys, single-digit
/// entries per key — exactly the InventoryService pattern). All operations are
/// O(entries-for-this-key); whole-map sweeps are O(distinct keys).
/// </summary>
public sealed class PendingCorrelator<TKey, TReq>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, List<Entry>> _buckets;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly Action<TKey, TReq>? _onUnmatched;

    private readonly record struct Entry(TReq Value, DateTime EnqueuedAt);

    /// <summary>
    /// Construct a correlator that evicts entries older than <paramref name="ttl"/>.
    /// </summary>
    /// <param name="ttl">
    /// Arrival-window TTL. Entries enqueued via <see cref="Add"/> are eligible
    /// for eviction once <c>now - EnqueuedAt</c> exceeds this span. Must be
    /// positive.
    /// </param>
    /// <param name="time">
    /// Time source for enqueue stamping and TTL evaluation. Defaults to
    /// <see cref="TimeProvider.System"/>. Injecting a fake provider makes
    /// tests deterministic without sleeping.
    /// </param>
    /// <param name="onUnmatched">
    /// Optional callback fired once per entry that is evicted by the TTL pass
    /// without being matched. Invoked outside the internal lock. Pass
    /// <c>null</c> to silently drop unmatched entries (the pre-existing
    /// InventoryService behaviour).
    /// </param>
    /// <param name="keyComparer">
    /// Optional equality comparer for keys. Defaults to
    /// <see cref="EqualityComparer{TKey}.Default"/>; string callers typically
    /// pass <see cref="StringComparer.Ordinal"/>.
    /// </param>
    public PendingCorrelator(
        TimeSpan ttl,
        TimeProvider? time = null,
        Action<TKey, TReq>? onUnmatched = null,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");
        _ttl = ttl;
        _time = time ?? TimeProvider.System;
        _onUnmatched = onUnmatched;
        _buckets = new Dictionary<TKey, List<Entry>>(keyComparer);
    }

    /// <summary>
    /// Number of currently-stored entries across all keys. Reflects the raw
    /// store — stale entries are counted until eviction (lazily on access or
    /// explicitly via <see cref="DrainStale"/>).
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                int total = 0;
                foreach (var list in _buckets.Values) total += list.Count;
                return total;
            }
        }
    }

    /// <summary>
    /// Enqueue a pending request under <paramref name="key"/>. Captures the
    /// enqueue time from the injected <see cref="TimeProvider"/> at call time.
    /// Multiple requests under the same key are retained in FIFO order.
    /// </summary>
    /// <remarks>
    /// Invariant: the per-key bucket is in non-decreasing <c>EnqueuedAt</c>
    /// order because <see cref="Add"/> is the only writer and always appends
    /// with a freshly-read <c>now</c>. Eviction relies on this — it scans the
    /// head of the bucket and stops at the first non-stale entry. A future
    /// caller that inserted mid-bucket or used a non-monotonic
    /// <see cref="TimeProvider"/> would break the invariant and silently leave
    /// stale entries behind.
    /// </remarks>
    public void Add(TKey key, TReq request)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = _time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out var list))
            {
                list = new List<Entry>();
                _buckets[key] = list;
            }
            list.Add(new Entry(request, now));
        }
    }

    /// <summary>
    /// Pop the FIFO-oldest non-stale entry for <paramref name="key"/>. Stale
    /// entries at the front of the bucket are evicted along the way and routed
    /// through the unmatched callback before the surviving head is returned.
    /// Returns <c>false</c> if the bucket is empty (or empty after eviction).
    /// </summary>
    public bool TryTake(TKey key, [MaybeNullWhen(false)] out TReq request)
    {
        ArgumentNullException.ThrowIfNull(key);
        List<(TKey, TReq)>? evicted = null;
        bool found;
        TReq? taken;
        lock (_gate)
        {
            if (_buckets.TryGetValue(key, out var list))
            {
                EvictStale(key, list, ref evicted);
                if (list.Count > 0)
                {
                    taken = list[0].Value;
                    list.RemoveAt(0);
                    if (list.Count == 0) _buckets.Remove(key);
                    found = true;
                }
                else
                {
                    _buckets.Remove(key);
                    taken = default;
                    found = false;
                }
            }
            else
            {
                taken = default;
                found = false;
            }
        }
        FireUnmatched(evicted);
        request = found ? taken! : default;
        return found;
    }

    /// <summary>
    /// Explicit eviction pass across every key. Each entry whose age exceeds
    /// the TTL is removed and routed through the unmatched callback (if any).
    /// Empty buckets are pruned from the dictionary so the working set tracks
    /// the live set, not historic peaks.
    /// </summary>
    public void DrainStale()
    {
        List<(TKey, TReq)>? evicted = null;
        lock (_gate)
        {
            if (_buckets.Count == 0) return;
            List<TKey>? empties = null;
            foreach (var (key, list) in _buckets)
            {
                EvictStale(key, list, ref evicted);
                if (list.Count == 0) (empties ??= new()).Add(key);
            }
            if (empties is not null)
                foreach (var k in empties) _buckets.Remove(k);
        }
        FireUnmatched(evicted);
    }

    /// <summary>MUST be called with <see cref="_gate"/> held. Removes stale
    /// entries from the front of <paramref name="list"/> and appends each
    /// evicted entry to <paramref name="evicted"/> for post-lock callback
    /// dispatch.</summary>
    private void EvictStale(TKey key, List<Entry> list, ref List<(TKey, TReq)>? evicted)
    {
        if (list.Count == 0) return;
        var now = _time.GetUtcNow().UtcDateTime;
        int firstAlive = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (now - list[i].EnqueuedAt <= _ttl)
            {
                firstAlive = i;
                break;
            }
        }
        if (firstAlive == 0) return; // nothing stale
        int evictCount = firstAlive < 0 ? list.Count : firstAlive;
        if (_onUnmatched is not null)
        {
            evicted ??= new List<(TKey, TReq)>();
            for (int i = 0; i < evictCount; i++) evicted.Add((key, list[i].Value));
        }
        list.RemoveRange(0, evictCount);
    }

    /// <summary>Invokes the unmatched callback on each evicted entry. Runs
    /// outside <see cref="_gate"/> so a callback may safely re-enter the
    /// correlator. Exceptions from individual callbacks are aggregated and
    /// thrown after the sweep completes — the "explicit policy" guarantee
    /// (every evicted entry receives its callback) holds even when individual
    /// callbacks fault.</summary>
    private void FireUnmatched(List<(TKey Key, TReq Value)>? evicted)
    {
        if (evicted is null || _onUnmatched is null) return;
        List<Exception>? exceptions = null;
        foreach (var (k, v) in evicted)
        {
            try { _onUnmatched(k, v); }
            catch (Exception ex) { (exceptions ??= new List<Exception>()).Add(ex); }
        }
        if (exceptions is not null)
            throw new AggregateException(
                "One or more PendingCorrelator unmatched callbacks threw; the sweep continued and every evicted entry received its callback.",
                exceptions);
    }
}
