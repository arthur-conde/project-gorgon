using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
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
/// a non-virtualizing WrapPanel froze the UI thread. Issue #155 retires this
/// relevance predicate by reshaping QuestSource.Catalog to be the active set
/// directly; until then, the predicate filters at materialization time and
/// the source's coarse ProgressChanged event drives RecheckRelevance.
/// </summary>
public sealed partial class QuestTimersViewModel : ObservableObject, IDisposable
{
    private readonly QuestSource _source;
    private readonly DerivedTimerProgressService _derived;
    private readonly TimeProvider _time;
    private readonly TimerDisplayScheduler _scheduler;
    private readonly TimerSourceBinder _binder;
    private bool _disposed;

    [ObservableProperty] private QuestStateFilter _stateFilter = QuestStateFilter.All;

    public QuestTimersViewModel(QuestSource source, DerivedTimerProgressService derived, TimeProvider? time = null)
    {
        _source = source;
        _derived = derived;
        _time = time ?? TimeProvider.System;

        TimersView = CollectionViewSource.GetDefaultView(Timers);
        TimersView.Filter = ApplyFilter;
        TimersView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimerItemViewModel.GroupKey)));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.GroupKey), ListSortDirection.Ascending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.IsDone), ListSortDirection.Descending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.Name), ListSortDirection.Ascending));

        _scheduler = new TimerDisplayScheduler(_time);
        _binder = new TimerSourceBinder(
            _source, Timers, _time,
            isRelevant: IsRelevant,
            scheduler: _scheduler);

        _binder.RefreshRequired += (_, _) => TimersView.Refresh();
        _scheduler.RefreshRequired += (_, _) => TimersView.Refresh();

        // Pending-set changes don't mutate catalog or progress (so they
        // don't fire RowsChanged with non-empty deltas), but they shift
        // relevance for every catalog row. Wire the source's coarse
        // ProgressChanged to a relevance recheck — the small over-fire on
        // actual progress changes is acceptable until #155 retires this
        // entire relevance machinery.
        _source.ProgressChanged += OnSourceProgressChanged;
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

    private bool IsRelevant(TimerCatalogEntry entry, TimerProgressEntry? progress)
    {
        // Cooling or Done (any progress not dismissed) is always relevant.
        if (progress is { DismissedAt: null }) return true;
        // Otherwise must be in journal.
        if (entry.SourceMetadata is QuestCatalogPayload payload &&
            _source.PendingInternalNames.Contains(payload.Quest.InternalName)) return true;
        return false;
    }

    private void OnSourceProgressChanged(object? sender, EventArgs e) =>
        _binder.RecheckRelevance();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.ProgressChanged -= OnSourceProgressChanged;
        _binder.Dispose();
        _scheduler.Dispose();
    }
}

public enum QuestStateFilter { All, Pending, Cooling, Ready }

public static class QuestStateFilterValues
{
    public static IReadOnlyList<QuestStateFilter> All { get; } = Enum.GetValues<QuestStateFilter>();
}
