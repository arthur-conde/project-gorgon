using Gandalf.Domain;
using Mithril.Shared.Character;

namespace Gandalf.Services;

/// <summary>
/// Per-character progress store for derived (log-observed) timer sources — Quest,
/// Loot, and any future feed where the cooldown clock anchors on the *log-line
/// timestamp* rather than user-click-time. <see cref="Start(string,string,DateTimeOffset)"/>
/// takes an explicit <c>startedAt</c> so log-replay produces correct elapsed times
/// (a chest looted 90 minutes ago should not show a freshly-restarted 3-hour
/// cooldown).
///
/// Sibling to <see cref="TimerProgressService"/> — derived rows have different
/// lifecycle semantics (DismissedAt instead of CompletedAt; domain-string keys
/// instead of GUIDs; GC'd on catalog removal instead of persisting forever) so
/// they live in their own per-character file with their own service instance.
/// </summary>
public sealed class DerivedTimerProgressService : IDisposable
{
    private readonly PerCharacterView<DerivedProgress> _view;
    private readonly TimeProvider _time;
    private readonly System.Timers.Timer _debounce;
    private readonly Lock _flushLock = new();
    private bool _dirty;

    public DerivedTimerProgressService(
        PerCharacterView<DerivedProgress> view,
        TimeProvider? time = null)
    {
        _view = view;
        _time = time ?? TimeProvider.System;
        _view.CurrentChanged += OnCurrentChanged;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
    }

    public event EventHandler? ProgressChanged;

    /// <summary>
    /// Per-source row state for the active character. Returns an empty map when no
    /// character is active or the source has never written a row.
    /// </summary>
    public IReadOnlyDictionary<string, DerivedTimerProgress> SnapshotFor(string sourceId)
    {
        var current = _view.Current;
        if (current is null) return EmptyMap;
        return current.BySource.TryGetValue(sourceId, out var inner)
            ? inner
            : EmptyMap;
    }

    public DerivedTimerProgress? GetProgress(string sourceId, string key)
    {
        var current = _view.Current;
        if (current is null) return null;
        return current.BySource.TryGetValue(sourceId, out var inner)
               && inner.TryGetValue(key, out var p)
            ? p
            : null;
    }

    /// <summary>
    /// Stamp <c>StartedAt = startedAt</c> (a log-line timestamp, typically in the
    /// past) and clear any prior <c>DismissedAt</c>. Re-observation overwrites — a
    /// re-loot or quest re-completion replaces the previous cooldown clock.
    /// </summary>
    public void Start(string sourceId, string key, DateTimeOffset startedAt)
    {
        var current = _view.Current;
        if (current is null) return;
        var inner = EnsureSourceMap(current, sourceId);
        if (!inner.TryGetValue(key, out var progress))
        {
            progress = new DerivedTimerProgress();
            inner[key] = progress;
        }
        progress.StartedAt = startedAt;
        progress.DismissedAt = null;
        SaveNow();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Alias for <see cref="Start"/>. Derived sources don't distinguish
    /// "first start" from "restart after done" the way the user feed does — every
    /// observation is the same operation, semantically. Provided for symmetry with
    /// <see cref="TimerProgressService"/>.
    /// </summary>
    public void Restart(string sourceId, string key, DateTimeOffset startedAt) =>
        Start(sourceId, key, startedAt);

    /// <summary>
    /// Stamp <c>DismissedAt = now</c> (TimeProvider clock). The row stays in
    /// storage so the next observation can resurrect it with a fresh cooldown
    /// — that's the cross-source "ready and unacked" model.
    /// </summary>
    public void Dismiss(string sourceId, string key)
    {
        var current = _view.Current;
        if (current is null) return;
        if (!current.BySource.TryGetValue(sourceId, out var inner)) return;
        if (!inner.TryGetValue(key, out var progress)) return;
        progress.DismissedAt = _time.GetUtcNow();
        SaveNow();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drop progress entries whose key is no longer in the live catalog. Called by
    /// each source on <c>CatalogChanged</c> so removed quests/chests don't leak
    /// progress rows forever.
    /// </summary>
    public void GarbageCollect(string sourceId, IEnumerable<string> validKeys)
    {
        var current = _view.Current;
        if (current is null) return;
        if (!current.BySource.TryGetValue(sourceId, out var inner)) return;

        var keep = new HashSet<string>(validKeys, StringComparer.Ordinal);
        var changed = false;
        foreach (var key in inner.Keys.ToArray())
        {
            if (keep.Contains(key)) continue;
            inner.Remove(key);
            changed = true;
        }
        if (!changed) return;
        MarkDirty();
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCurrentChanged(object? sender, EventArgs e) =>
        ProgressChanged?.Invoke(this, EventArgs.Empty);

    private static Dictionary<string, DerivedTimerProgress> EnsureSourceMap(
        DerivedProgress current, string sourceId)
    {
        if (!current.BySource.TryGetValue(sourceId, out var inner))
        {
            inner = new Dictionary<string, DerivedTimerProgress>(StringComparer.Ordinal);
            current.BySource[sourceId] = inner;
        }
        return inner;
    }

    private void MarkDirty()
    {
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Immediate save — bypasses debounce so derived progress survives a crash.</summary>
    private void SaveNow()
    {
        _debounce.Stop();
        _dirty = false;
        FlushCore();
    }

    private void Flush()
    {
        lock (_flushLock)
        {
            if (!_dirty) return;
            _dirty = false;
            FlushCore();
        }
    }

    private void FlushCore()
    {
        try { _view.Save(); } catch { /* best-effort */ }
    }

    public void Dispose()
    {
        _view.CurrentChanged -= OnCurrentChanged;
        _debounce.Stop();
        _debounce.Dispose();
        if (_dirty) { try { FlushCore(); } catch { } }
    }

    private static readonly IReadOnlyDictionary<string, DerivedTimerProgress> EmptyMap =
        new Dictionary<string, DerivedTimerProgress>(StringComparer.Ordinal);
}
