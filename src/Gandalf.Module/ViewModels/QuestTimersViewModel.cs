using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gandalf.Domain;
using Gandalf.Services;

namespace Gandalf.ViewModels;

/// <summary>
/// ViewModel for the Quests tab. Renders repeatable-quest cooldowns from
/// <see cref="QuestSource"/> and exposes the State filter chip
/// (Pending / Cooling / Ready / All) plus bulk dismiss commands.
///
/// Post-#155 the source's catalog IS the active set
/// (<c>IPlayerQuestJournalState.ActiveQuests ∪ keys-with-progress</c>) — no need for the
/// VM to re-filter the universe of repeatable quests, so the old
/// <c>IsRelevant</c> predicate and the <c>PendingChanged</c> subscription are
/// gone. Pending in the State filter just means <see cref="TimerState.Idle"/>:
/// a row in the catalog with no progress can only have arrived via
/// <c>ActiveQuests</c>, so Idle is exactly "in journal, not yet completed."
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
        _binder = new TimerSourceBinder(_source, Timers, _time, scheduler: _scheduler);

        _binder.RefreshRequired += (_, _) => TimersView.Refresh();
        _scheduler.RefreshRequired += (_, _) => TimersView.Refresh();
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
            QuestStateFilter.Pending => vm.State == TimerState.Idle,
            QuestStateFilter.Cooling => vm.State == TimerState.Running,
            QuestStateFilter.Ready => vm.State == TimerState.Done,
            _ => true,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _binder.Dispose();
        _scheduler.Dispose();
    }
}

public enum QuestStateFilter { All, Pending, Cooling, Ready }

public static class QuestStateFilterValues
{
    public static IReadOnlyList<QuestStateFilter> All { get; } = Enum.GetValues<QuestStateFilter>();
}
