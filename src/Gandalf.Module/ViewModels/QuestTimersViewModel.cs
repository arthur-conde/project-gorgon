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
/// </summary>
public sealed partial class QuestTimersViewModel : ObservableObject
{
    private readonly QuestSource _source;
    private readonly DerivedTimerProgressService _derived;
    private readonly DispatcherTimer _refreshTimer;

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
        // Pending: in the player's journal but not currently cooling.
        if (vm.Catalog.SourceMetadata is not QuestCatalogPayload payload) return false;
        if (vm.State != TimerState.Idle) return false;
        return _source.PendingInternalNames.Contains(payload.Quest.InternalName);
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

public enum QuestStateFilter { All, Pending, Cooling, Ready }

public static class QuestStateFilterValues
{
    public static IReadOnlyList<QuestStateFilter> All { get; } = Enum.GetValues<QuestStateFilter>();
}
