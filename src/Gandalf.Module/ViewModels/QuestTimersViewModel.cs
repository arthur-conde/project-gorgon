using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gandalf.Domain;
using Gandalf.Services;

namespace Gandalf.ViewModels;

/// <summary>
/// ViewModel for the Quests tab. Renders repeatable-quest cooldowns from the
/// shared <see cref="QuestSource"/> and exposes the State filter chip
/// (Pending / Cooling / Ready / All) plus bulk dismiss commands.
///
/// Materializes only the rows the player cares about: pending in journal, or
/// cooling/done. The full catalog is ~2,000 entries — projecting it all into
/// a non-virtualizing WrapPanel froze the UI thread.
/// </summary>
public sealed partial class QuestTimersViewModel : ObservableObject
{
    private readonly QuestSource _source;
    private readonly DerivedTimerProgressService _derived;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, TimerItemViewModel> _byKey = new(StringComparer.Ordinal);
    private Dictionary<string, TimerCatalogEntry> _catalogByKey = new(StringComparer.Ordinal);

    [ObservableProperty] private QuestStateFilter _stateFilter = QuestStateFilter.All;

    public QuestTimersViewModel(QuestSource source, DerivedTimerProgressService derived)
    {
        _source = source;
        _derived = derived;

        _source.CatalogChanged += (_, _) => Sync();
        _source.ProgressChanged += (_, _) => Sync();

        TimersView = CollectionViewSource.GetDefaultView(Timers);
        TimersView.Filter = ApplyFilter;
        TimersView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimerItemViewModel.GroupKey)));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.GroupKey), ListSortDirection.Ascending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.IsDone), ListSortDirection.Descending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.Name), ListSortDirection.Ascending));

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Tick();
        _refreshTimer.Start();

        Sync();
    }

    public ObservableCollection<TimerItemViewModel> Timers { get; } = [];
    public ICollectionView TimersView { get; }

    [RelayCommand]
    private void DismissAllReady()
    {
        foreach (var vm in Timers.Where(t => t.State == TimerState.Done).ToArray())
            _derived.Dismiss(_source.SourceId, vm.Key);
    }

    [RelayCommand]
    private void DismissAll()
    {
        foreach (var vm in Timers.ToArray())
            _derived.Dismiss(_source.SourceId, vm.Key);
    }

    [RelayCommand]
    private void DismissOne(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        _derived.Dismiss(_source.SourceId, vm.Key);
    }

    partial void OnStateFilterChanged(QuestStateFilter value) => TimersView.Refresh();

    private bool ApplyFilter(object obj)
    {
        if (obj is not TimerItemViewModel vm) return false;

        return StateFilter switch
        {
            QuestStateFilter.All => true,
            QuestStateFilter.Pending => IsPending(vm),
            QuestStateFilter.Cooling => vm.State == TimerState.Running,
            QuestStateFilter.Ready => vm.State == TimerState.Done,
            _ => true,
        };
    }

    private bool IsPending(TimerItemViewModel vm)
    {
        if (vm.Catalog.SourceMetadata is not QuestCatalogPayload payload) return false;
        if (vm.State != TimerState.Idle) return false;
        return _source.PendingInternalNames.Contains(payload.Quest.InternalName);
    }

    private static bool IsRelevant(TimerCatalogEntry entry, TimerProgressEntry? progress, IReadOnlySet<string> pending)
    {
        if (progress is { DismissedAt: null }) return true;
        if (entry.SourceMetadata is QuestCatalogPayload payload &&
            pending.Contains(payload.Quest.InternalName)) return true;
        return false;
    }

    /// <summary>
    /// Reconcile <see cref="Timers"/> against the source's relevant set. Diffs
    /// in place — adds new rows, updates existing rows, removes gone rows —
    /// instead of clearing the collection, which would thrash WPF's grouping.
    /// </summary>
    private void Sync()
    {
        _catalogByKey = _source.Catalog.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var progress = _source.Progress;
        var pending = _source.PendingInternalNames;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _source.Catalog)
        {
            progress.TryGetValue(entry.Key, out var p);
            if (!IsRelevant(entry, p, pending)) continue;

            seen.Add(entry.Key);
            if (_byKey.TryGetValue(entry.Key, out var vm))
            {
                vm.UpdateRow(new TimerRow(entry, p));
            }
            else
            {
                vm = new TimerItemViewModel(new TimerRow(entry, p));
                _byKey[entry.Key] = vm;
                Timers.Add(vm);
            }
        }

        for (var i = Timers.Count - 1; i >= 0; i--)
        {
            var vm = Timers[i];
            if (seen.Contains(vm.Key)) continue;
            Timers.RemoveAt(i);
            _byKey.Remove(vm.Key);
        }

        TimersView.Refresh();
    }

    /// <summary>
    /// Per-second update of progress fractions and time labels. Per-item
    /// <c>OnPropertyChanged</c> handles the bindings; only call
    /// <see cref="ICollectionView.Refresh"/> when an item's <c>State</c>
    /// transitioned (Running &harr; Done) — that changes filter / sort keys.
    /// </summary>
    internal void Tick()
    {
        var progress = _source.Progress;
        var stateChanged = false;

        foreach (var vm in _byKey.Values)
        {
            if (!_catalogByKey.TryGetValue(vm.Key, out var entry)) continue;
            progress.TryGetValue(vm.Key, out var p);
            var prior = vm.State;
            vm.UpdateRow(new TimerRow(entry, p));
            if (vm.State != prior) stateChanged = true;
        }

        if (stateChanged) TimersView.Refresh();
    }
}

public enum QuestStateFilter { All, Pending, Cooling, Ready }

public static class QuestStateFilterValues
{
    public static IReadOnlyList<QuestStateFilter> All { get; } = Enum.GetValues<QuestStateFilter>();
}
