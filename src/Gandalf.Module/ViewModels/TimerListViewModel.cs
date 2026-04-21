using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gandalf.Domain;
using Gandalf.Services;
using Gandalf.Views;
using Gorgon.Shared.Wpf.Dialogs;

namespace Gandalf.ViewModels;

public sealed partial class TimerListViewModel : ObservableObject
{
    private readonly TimerStateService _stateService;
    private readonly TimerAlarmService _alarmService;
    private readonly IDialogService _dialogService;
    private readonly DispatcherTimer _refreshTimer;

    public TimerListViewModel(
        TimerStateService stateService,
        TimerAlarmService alarmService,
        IDialogService dialogService)
    {
        _stateService = stateService;
        _alarmService = alarmService;
        _dialogService = dialogService;

        _stateService.TimerChanged += (_, _) => SyncFromState();

        TimersView = CollectionViewSource.GetDefaultView(Timers);
        TimersView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimerItemViewModel.GroupKey)));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.GroupKey), ListSortDirection.Ascending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.IsIdle), ListSortDirection.Descending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.IsDone), ListSortDirection.Ascending));

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Tick();
        _refreshTimer.Start();

        SyncFromState();
    }

    public ObservableCollection<TimerItemViewModel> Timers { get; } = [];
    public ICollectionView TimersView { get; }

    public ObservableCollection<string> KnownRegions { get; } = [];
    public ObservableCollection<string> KnownMaps { get; } = [];

    [RelayCommand]
    private void AddTimer()
    {
        RefreshAutocomplete();
        var vm = new TimerDialogViewModel(null, KnownRegions, KnownMaps);
        var content = new TimerDialogContent();

        if (_dialogService.ShowDialog(vm, content) != true) return;

        var timer = new GandalfTimer
        {
            Name = vm.ResultName,
            Duration = vm.ResultDuration,
            Region = vm.ResultRegion,
            Map = vm.ResultMap,
        };
        _stateService.Add(timer);
    }

    [RelayCommand]
    private void EditTimer(TimerItemViewModel? item)
    {
        if (item is null) return;

        RefreshAutocomplete();
        var vm = new TimerDialogViewModel(item.Timer, KnownRegions, KnownMaps);
        var content = new TimerDialogContent();

        if (_dialogService.ShowDialog(vm, content) != true) return;

        _stateService.Update(item.Id, t =>
        {
            t.Name = vm.ResultName;
            t.Region = vm.ResultRegion;
            t.Map = vm.ResultMap;
            if (t.State == TimerState.Idle)
            {
                var duration = vm.ResultDuration;
                if (duration > TimeSpan.Zero)
                    t.Duration = duration;
            }
        });
    }

    [RelayCommand]
    private void StartTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        _stateService.Start(vm.Id);
    }

    [RelayCommand]
    private void RestartTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        _stateService.Restart(vm.Id);
    }

    [RelayCommand]
    private void DeleteTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        _stateService.Remove(vm.Id);
    }

    [RelayCommand]
    private void ClearDone() => _stateService.ClearCompleted();

    [RelayCommand]
    private void CopyTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        var json = TimerClipboard.Serialize([vm.Timer]);
        try { Clipboard.SetText(json); } catch { }
    }

    [RelayCommand]
    private void PasteTimers()
    {
        string text;
        try { text = Clipboard.GetText(); } catch { return; }
        if (string.IsNullOrWhiteSpace(text)) return;

        var entries = TimerClipboard.TryDeserialize(text);
        if (entries is null || entries.Count == 0) return;

        foreach (var entry in entries)
        {
            var timer = TimerClipboard.ToTimer(entry);
            if (timer is not null)
                _stateService.Add(timer);
        }
    }

    [RelayCommand]
    private void SnoozeAll() => _alarmService.SnoozeAll();

    [RelayCommand]
    private void DismissAll() => _alarmService.DismissAll();

    private void SyncFromState()
    {
        Timers.Clear();
        foreach (var t in _stateService.Timers)
            Timers.Add(new TimerItemViewModel(t));
        TimersView.Refresh();
    }

    private void RefreshAutocomplete()
    {
        var regions = _stateService.Timers
            .Select(t => t.Region)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r);

        KnownRegions.Clear();
        foreach (var r in regions) KnownRegions.Add(r);

        var maps = _stateService.Timers
            .Select(t => t.Map)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m);

        KnownMaps.Clear();
        foreach (var m in maps) KnownMaps.Add(m);
    }

    private void Tick()
    {
        _stateService.CheckExpirations();
        foreach (var vm in Timers) vm.Refresh();
    }
}
