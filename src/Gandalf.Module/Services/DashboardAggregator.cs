using Gandalf.Domain;

namespace Gandalf.Services;

/// <summary>
/// Cross-source aggregator. Subscribes to every registered <see cref="ITimerSource"/>'s
/// <c>CatalogChanged</c> / <c>ProgressChanged</c> events and recomputes a flat
/// list of <see cref="TimerSummary"/> rows. The Dashboard tab consumes the
/// aggregator's <see cref="Updated"/> event to refresh its three sections.
///
/// No caching beyond the projection itself — recomputation is cheap relative
/// to the dashboard's 1Hz refresh budget. Driven by <c>TimeProvider</c> so
/// tests can advance time deterministically.
/// </summary>
public sealed class DashboardAggregator : IDisposable
{
    private readonly IReadOnlyList<ITimerSource> _sources;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private IReadOnlyList<TimerSummary> _summaries = [];

    public DashboardAggregator(IEnumerable<ITimerSource> sources, TimeProvider? time = null)
    {
        _sources = sources.ToArray();
        _time = time ?? TimeProvider.System;

        foreach (var source in _sources)
        {
            source.CatalogChanged += OnSourceChanged;
            source.ProgressChanged += OnSourceChanged;
        }

        Recompute();
    }

    /// <summary>Fires whenever any source's catalog or progress changes.</summary>
    public event EventHandler? Updated;

    public IReadOnlyList<TimerSummary> Summaries
    {
        get { lock (_lock) return _summaries; }
    }

    /// <summary>
    /// Recomputes <see cref="Summaries"/> using the current TimeProvider clock.
    /// Called automatically on source events; call manually from the dashboard's
    /// 1Hz tick so Cooling → Ready transitions show up without a source event.
    /// </summary>
    public void Recompute()
    {
        var now = _time.GetUtcNow();
        var list = new List<TimerSummary>();
        foreach (var source in _sources)
        {
            var progress = source.Progress;
            foreach (var entry in source.Catalog)
            {
                progress.TryGetValue(entry.Key, out var p);
                list.Add(BuildSummary(source.SourceId, entry, p, now));
            }
        }

        lock (_lock) _summaries = list;
        Updated?.Invoke(this, EventArgs.Empty);
    }

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

    private void OnSourceChanged(object? sender, EventArgs e) => Recompute();

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.CatalogChanged -= OnSourceChanged;
            source.ProgressChanged -= OnSourceChanged;
        }
    }
}
