using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Gandalf.Domain;
using Gandalf.ViewModels;

namespace Gandalf.Services;

/// <summary>
/// Generalisation of the diff-in-place pattern from
/// <c>QuestTimersViewModel.Sync</c>. Binds an <see cref="ITimerSource"/> to a
/// host VM's <see cref="ObservableCollection{TimerItemViewModel}"/> by
/// translating <see cref="ITimerSource.RowsChanged"/> deltas into add /
/// update / remove operations on the target collection — so consumers stop
/// clearing-and-rebuilding on every event.
///
/// <para>Threading: source events fire from the log-ingestion background
/// thread, but the target collection is bound to a WPF
/// <see cref="System.Windows.Data.CollectionView"/> that requires UI-thread
/// mutations (see <c>memory/wpf_crossthread_collection_mutations.md</c> —
/// Pippin's GourmandViewModel was the prior incident). The binder captures
/// the active dispatcher in its ctor and routes every collection mutation
/// through <c>CheckAccess</c> + <c>BeginInvoke</c>; tests construct without
/// an <see cref="Application"/> so the fallback <see cref="Dispatcher.CurrentDispatcher"/>
/// stays on the test thread and applies inline.</para>
///
/// <para>Relevance filter: optional predicate (used by QuestTimersViewModel
/// to skip the ~2,000 repeatable quests the player has never touched).
/// LootSource and UserTimerSource pass null — every catalog row materialises.
/// When the predicate's external inputs change (the QuestSource pending set
/// today; <see cref="Mithril.Shared.Quests.IQuestService"/> in the planned
/// follow-up), the host VM calls <see cref="RecheckRelevance"/>.</para>
///
/// <para>Refresh signal: fires <see cref="RefreshRequired"/> at the end of
/// each delta batch when an existing row's GroupKey or State changed (the
/// properties used by the tab VMs' sort / group / filter descriptors).
/// CollectionView automatically picks up Add / Remove via
/// <c>CollectionChanged</c>, so those don't need an explicit signal —
/// only in-place property mutations do.</para>
/// </summary>
public sealed class TimerSourceBinder : IDisposable
{
    private readonly ITimerSource _source;
    private readonly ObservableCollection<TimerItemViewModel> _target;
    private readonly TimeProvider _clock;
    private readonly Func<TimerCatalogEntry, TimerProgressEntry?, bool> _isRelevant;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, TimerItemViewModel> _byKey =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public TimerSourceBinder(
        ITimerSource source,
        ObservableCollection<TimerItemViewModel> target,
        TimeProvider clock,
        Func<TimerCatalogEntry, TimerProgressEntry?, bool>? isRelevant = null,
        Dispatcher? dispatcher = null)
    {
        _source = source;
        _target = target;
        _clock = clock;
        _isRelevant = isRelevant ?? ((_, _) => true);
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        // Initial sync runs on whichever thread constructed the binder —
        // typically the UI thread (host VM ctor). Subsequent deltas marshal
        // through OnRowsChanged.
        ApplyRecheckRelevance(initial: true);
        _source.RowsChanged += OnRowsChanged;
    }

    /// <summary>
    /// Snapshot of the rows currently materialised, keyed by <see cref="TimerCatalogEntry.Key"/>.
    /// Exposed so the host VM can iterate (e.g. for bulk dismiss commands)
    /// without re-walking the source.
    /// </summary>
    public IReadOnlyDictionary<string, TimerItemViewModel> ByKey => _byKey;

    /// <summary>
    /// Fires after a delta batch (or <see cref="RecheckRelevance"/>) when at
    /// least one in-place property change requires the host's
    /// <c>ICollectionView</c> to re-run filter / sort / group.
    /// Add / Remove are handled by <c>CollectionChanged</c> automatically and
    /// do not raise this event.
    /// </summary>
    public event EventHandler? RefreshRequired;

    /// <summary>
    /// Re-evaluate the relevance predicate against every catalog row. Call
    /// when an external input to the predicate has changed (e.g. the
    /// QuestSource pending set, after #155 lands the <c>IQuestService</c>
    /// active-set reshape removes the need for this entirely).
    /// </summary>
    public void RecheckRelevance()
    {
        Dispatch(() => ApplyRecheckRelevance(initial: false));
    }

    private void ApplyRecheckRelevance(bool initial)
    {
        if (_disposed) return;

        var anyRefreshNeeded = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in _source.Catalog)
        {
            seen.Add(entry.Key);
            _source.TryGetProgress(entry.Key, out var progress);
            anyRefreshNeeded |= ApplyKey(entry.Key, entry, progress, isCatalogChange: true);
        }

        // Drop any materialised row whose key is no longer in the catalog.
        foreach (var staleKey in _byKey.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            RemoveExistingRow(staleKey);
            anyRefreshNeeded = true;
        }

        // The initial sync runs from the ctor — host VM hasn't subscribed
        // to RefreshRequired yet, so suppress the signal there. The host
        // calls TimersView.Refresh once after construction anyway.
        if (anyRefreshNeeded && !initial) RaiseRefreshRequired();
    }

    private void OnRowsChanged(object? sender, TimerRowsChangedEventArgs e)
    {
        // Snapshot deltas before dispatching — the source may mutate further
        // before the dispatcher pump gets to the work item.
        var deltas = e.Deltas;
        Dispatch(() => ApplyDeltas(deltas));
    }

    private void ApplyDeltas(IReadOnlyList<TimerRowDelta> deltas)
    {
        if (_disposed) return;

        var anyRefreshNeeded = false;
        foreach (var delta in deltas)
        {
            switch (delta.Kind)
            {
                case TimerRowChangeKind.Added:
                case TimerRowChangeKind.CatalogChanged:
                case TimerRowChangeKind.ProgressChanged:
                    if (delta.Catalog is null) break;  // defensive — Added/*Changed always carry catalog
                    anyRefreshNeeded |= ApplyKey(delta.Key, delta.Catalog, delta.Progress, isCatalogChange: delta.Kind != TimerRowChangeKind.ProgressChanged);
                    break;

                case TimerRowChangeKind.Removed:
                    if (_byKey.ContainsKey(delta.Key))
                    {
                        RemoveExistingRow(delta.Key);
                        anyRefreshNeeded = true;
                    }
                    break;
            }
        }

        if (anyRefreshNeeded) RaiseRefreshRequired();
    }

    /// <summary>
    /// Apply one row delta. Returns true iff an in-place property change
    /// happened that warrants <see cref="RefreshRequired"/> (a state or
    /// group-key transition on an existing row). Add / Remove are reported
    /// as needing a refresh too because at minimum the host's filter-chip
    /// counters may want to refresh, and the cost of one extra
    /// <c>ICollectionView.Refresh</c> per batch is negligible.
    /// </summary>
    private bool ApplyKey(
        string key,
        TimerCatalogEntry entry,
        TimerProgressEntry? progress,
        bool isCatalogChange)
    {
        var relevant = _isRelevant(entry, progress);
        var hadRow = _byKey.TryGetValue(key, out var vm);

        if (!relevant)
        {
            if (hadRow)
            {
                RemoveExistingRow(key);
                return true;
            }
            return false;
        }

        if (!hadRow)
        {
            var fresh = new TimerItemViewModel(new TimerRow(entry, progress) { Clock = _clock });
            _byKey[key] = fresh;
            _target.Add(fresh);
            return true;
        }

        // Existing row: capture sort/group inputs before update, decide
        // whether the host needs to Refresh after.
        var oldGroupKey = vm!.GroupKey;
        var oldState = vm.State;
        vm.UpdateRow(new TimerRow(entry, progress) { Clock = _clock });
        return vm.GroupKey != oldGroupKey || vm.State != oldState;
    }

    private void RemoveExistingRow(string key)
    {
        if (!_byKey.Remove(key, out var vm)) return;
        _target.Remove(vm);
    }

    private void RaiseRefreshRequired() =>
        RefreshRequired?.Invoke(this, EventArgs.Empty);

    private void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RowsChanged -= OnRowsChanged;
    }
}
