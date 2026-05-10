using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gandalf.Domain;
using Gandalf.Services;

namespace Gandalf.ViewModels;

/// <summary>
/// ViewModel for the Loot tab. Renders chest + defeat cooldown rows from the
/// shared <see cref="LootSource"/> and exposes Kind / State filter chips and
/// bulk dismiss commands.
/// </summary>
public sealed partial class LootTimersViewModel : ObservableObject, IDisposable
{
    private readonly LootSource _source;
    private readonly DerivedTimerProgressService _derived;
    private readonly TimeProvider _time;
    private readonly TimerDisplayScheduler _scheduler;
    private readonly TimerSourceBinder _binder;
    private bool _disposed;

    [ObservableProperty] private LootKindFilter _kindFilter = LootKindFilter.All;
    [ObservableProperty] private LootStateFilter _stateFilter = LootStateFilter.All;

    public LootTimersViewModel(LootSource source, DerivedTimerProgressService derived, TimeProvider? time = null)
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

        // Both signals trigger a single CollectionView refresh — debounced
        // implicitly by the dispatcher (consecutive Refresh calls collapse).
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

    /// <summary>
    /// Hard-delete: drop the catalog cache entry and the progress row outright.
    /// Distinct from <see cref="DismissOne"/>, which only stamps
    /// <c>DismissedAt</c> and lets re-observation resurrect the row.
    /// Intended for false-positive entries the bracket tracker accreted before
    /// its discrimination signals were complete.
    /// </summary>
    [RelayCommand]
    private void RemoveOne(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        _source.Forget(vm.Key);
    }

    partial void OnKindFilterChanged(LootKindFilter value) => TimersView.Refresh();
    partial void OnStateFilterChanged(LootStateFilter value) => TimersView.Refresh();

    private bool ApplyFilter(object obj)
    {
        if (obj is not TimerItemViewModel vm) return false;

        if (KindFilter != LootKindFilter.All)
        {
            if (vm.Catalog.SourceMetadata is not LootCatalogPayload p) return false;
            if (KindFilter == LootKindFilter.Chests && p.Kind != LootKind.Chest) return false;
            if (KindFilter == LootKindFilter.Defeats && p.Kind != LootKind.Defeat) return false;
        }

        if (StateFilter == LootStateFilter.Cooling && vm.State != TimerState.Running) return false;
        if (StateFilter == LootStateFilter.Ready && vm.State != TimerState.Done) return false;

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _binder.Dispose();
        _scheduler.Dispose();
    }
}

public enum LootKindFilter { All, Chests, Defeats }
public enum LootStateFilter { All, Cooling, Ready }

public static class LootKindFilterValues
{
    public static IReadOnlyList<LootKindFilter> All { get; } =
        Enum.GetValues<LootKindFilter>();
}

public static class LootStateFilterValues
{
    public static IReadOnlyList<LootStateFilter> All { get; } =
        Enum.GetValues<LootStateFilter>();
}
