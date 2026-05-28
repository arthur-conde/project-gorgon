using System;
using System.Collections.Generic;
using System.Linq;

namespace Mithril.Shared.Telemetry.Catalog;

/// <summary>
/// Thread-safe bounded set of tag keys that the scrubber encountered but
/// the <see cref="TagCatalog"/> does not know about. Bounded to keep memory
/// pressure constant — eviction order is FIFO by first-observation.
///
/// Exists so the settings tag-cloud UI can surface a "Newly seen" panel
/// without the catalog needing to know about new producer tags at build
/// time. The corresponding tag value is dropped from export until the user
/// promotes the chip — fail-closed by design.
/// </summary>
public sealed class NewlySeenTagsObserver
{
    private readonly object _lock = new();
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly int _capacity;

    /// <summary>Raised the first time a given key is observed.</summary>
    public event Action<string>? OnNewKey;

    /// <summary>
    /// Create a new observer.
    /// </summary>
    /// <param name="capacity">Maximum number of keys retained; oldest are evicted FIFO.</param>
    public NewlySeenTagsObserver(int capacity = 256) => _capacity = capacity;

    /// <summary>Record an observation of <paramref name="key"/>. No-op if already seen.</summary>
    public void Note(string key)
    {
        bool fresh;
        lock (_lock)
        {
            if (_set.Contains(key))
            {
                return;
            }
            _set.Add(key);
            _order.AddLast(key);
            while (_order.Count > _capacity)
            {
                var evicted = _order.First!.Value;
                _order.RemoveFirst();
                _set.Remove(evicted);
            }
            fresh = true;
        }
        if (fresh)
        {
            OnNewKey?.Invoke(key);
        }
    }

    /// <summary>Snapshot of currently-tracked keys in first-observation order.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            return _order.ToList();
        }
    }
}
