using System.Windows;
using System.Windows.Threading;
using Gandalf.Domain;

namespace Gandalf.Services;

/// <summary>
/// Cross-source aggregator. Subscribes to every registered <see cref="ITimerSource"/>'s
/// <see cref="ITimerSource.RowsChanged"/> per-key feed and applies deltas
/// incrementally to a flat <see cref="TimerSummary"/> map. The Dashboard
/// view consumes <see cref="Updated"/> to refresh its three sections.
///
/// <para>State on a summary is computed against <see cref="TimeProvider"/>
/// at projection time — pure time progression doesn't fire any source
/// event, so the Dashboard view's 1 Hz tick still calls
/// <see cref="Recompute"/> to surface <c>Cooling → Ready</c> transitions.
/// Driving that tick from a <see cref="Domain.TimerRow.NextDisplayChangeAt"/>-style
/// schedule on the aggregator is a clean follow-up; out of scope for the
/// timer-model PR.</para>
///
/// <para>Threading: source <see cref="ITimerSource.RowsChanged"/> events
/// fire on whichever thread the source happened to mutate on — for
/// QuestSource/LootSource that's the log-tail background thread when a
/// completion is observed live. <see cref="Updated"/> consumers
/// (<c>DashboardViewModel.Refresh</c>) mutate WPF-bound
/// <c>ObservableCollection</c>s, which require the UI thread. We capture
/// a <see cref="Dispatcher"/> in the ctor and marshal the entire delta
/// application + <see cref="Updated"/> fire through it. Without this,
/// CollectionView throws <c>"...does not support changes...from a
/// thread different from the Dispatcher thread"</c>, and worse — that
/// throw aborts the multicast invocation, so subsequent
/// <see cref="ITimerSource.RowsChanged"/> subscribers (the per-tab
/// <see cref="TimerSourceBinder"/>) never run and tab rows go stale
/// (issue #191; see <c>memory/wpf_crossthread_collection_mutations.md</c>).</para>
/// </summary>
public sealed class DashboardAggregator : IDisposable
{
    private readonly IReadOnlyList<ITimerSource> _sources;
    private readonly TimeProvider _time;
    private readonly Dispatcher _dispatcher;
    private readonly object _lock = new();
    private readonly Dictionary<(string SourceId, string Key), TimerSummary> _byKey = [];
    private IReadOnlyList<TimerSummary> _summaries = [];

    public DashboardAggregator(
        IEnumerable<ITimerSource> sources,
        TimeProvider? time = null,
        Dispatcher? dispatcher = null)
    {
        _sources = sources.ToArray();
        _time = time ?? TimeProvider.System;
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        var now = _time.GetUtcNow();
        foreach (var source in _sources)
        {
            foreach (var entry in source.Catalog)
            {
                source.TryGetProgress(entry.Key, out var progress);
                _byKey[(source.SourceId, entry.Key)] =
                    BuildSummary(source.SourceId, entry, progress, now);
            }
            source.RowsChanged += OnSourceRowsChanged;
        }

        SnapshotSummaries();
    }

    /// <summary>Fires whenever any source's deltas land or <see cref="Recompute"/> runs.</summary>
    public event EventHandler? Updated;

    public IReadOnlyList<TimerSummary> Summaries
    {
        get { lock (_lock) return _summaries; }
    }

    /// <summary>
    /// Re-evaluates state for every summary using the current
    /// <see cref="TimeProvider"/> clock. Source events handle catalog +
    /// progress mutations incrementally; this method exists for the
    /// dashboard's 1 Hz tick to catch <c>Cooling → Ready</c> transitions
    /// that fire on time alone (no source event).
    /// </summary>
    public void Recompute()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var source in _sources)
            {
                foreach (var entry in source.Catalog)
                {
                    source.TryGetProgress(entry.Key, out var progress);
                    _byKey[(source.SourceId, entry.Key)] =
                        BuildSummary(source.SourceId, entry, progress, now);
                }
            }
            SnapshotSummaries();
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourceRowsChanged(object? sender, TimerRowsChangedEventArgs e)
    {
        if (sender is not ITimerSource source) return;

        // Snapshot deltas before dispatch — the source may mutate further
        // before the dispatcher pump gets to the work item. Mirrors the
        // TimerSourceBinder pattern.
        var deltas = e.Deltas;
        Dispatch(() => ApplyDeltas(source, deltas));
    }

    private void ApplyDeltas(ITimerSource source, IReadOnlyList<TimerRowDelta> deltas)
    {
        var any = false;
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var delta in deltas)
            {
                var compositeKey = (source.SourceId, delta.Key);
                if (delta.Kind == TimerRowChangeKind.Removed)
                {
                    if (_byKey.Remove(compositeKey)) any = true;
                    continue;
                }
                if (delta.Catalog is null) continue;

                _byKey[compositeKey] = BuildSummary(source.SourceId, delta.Catalog, delta.Progress, now);
                any = true;
            }
            if (any) SnapshotSummaries();
        }
        if (any) Updated?.Invoke(this, EventArgs.Empty);
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    private void SnapshotSummaries() => _summaries = _byKey.Values.ToArray();

    private static TimerSummary BuildSummary(
        string sourceId,
        TimerCatalogEntry entry,
        TimerProgressEntry? progress,
        DateTimeOffset now)
    {
        if (progress is null || progress.DismissedAt is not null)
        {
            return new TimerSummary(sourceId, entry.Key, entry.DisplayName, entry.Region,
                ExpiresAt: null, State: TimerState.Idle);
        }

        var expiresAt = progress.StartedAt + entry.Duration;
        var state = expiresAt <= now ? TimerState.Done : TimerState.Running;
        return new TimerSummary(sourceId, entry.Key, entry.DisplayName, entry.Region,
            ExpiresAt: expiresAt, State: state);
    }

    public void Dispose()
    {
        foreach (var source in _sources)
            source.RowsChanged -= OnSourceRowsChanged;
    }
}
