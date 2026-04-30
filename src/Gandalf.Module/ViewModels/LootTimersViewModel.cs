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
/// ViewModel for the Loot tab. Renders chest + defeat cooldown rows from the
/// shared <see cref="LootSource"/> and exposes Kind / State filter chips and
/// bulk dismiss commands.
/// </summary>
public sealed partial class LootTimersViewModel : ObservableObject
{
    private readonly LootSource _source;
    private readonly DerivedTimerProgressService _derived;
    private readonly DispatcherTimer _refreshTimer;

    [ObservableProperty] private LootKindFilter _kindFilter = LootKindFilter.All;
    [ObservableProperty] private LootStateFilter _stateFilter = LootStateFilter.All;

    public LootTimersViewModel(LootSource source, DerivedTimerProgressService derived)
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

    private void Sync()
    {
        Timers.Clear();
        var progress = _source.Progress;
        foreach (var entry in _source.Catalog)
        {
            progress.TryGetValue(entry.Key, out var p);
            Timers.Add(new TimerItemViewModel(new TimerRow(entry, p)));
        }
        TimersView.Refresh();
    }

    private void Tick()
    {
        var progress = _source.Progress;
        var catalog = _source.Catalog.ToDictionary(c => c.Key, StringComparer.Ordinal);
        foreach (var vm in Timers)
        {
            if (!catalog.TryGetValue(vm.Key, out var entry)) continue;
            progress.TryGetValue(vm.Key, out var p);
            vm.UpdateRow(new TimerRow(entry, p));
        }
        TimersView.Refresh();
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
