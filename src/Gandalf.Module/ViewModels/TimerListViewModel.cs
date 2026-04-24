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
    private readonly TimerDefinitionsService _defs;
    private readonly TimerProgressService _progress;
    private readonly TimerAlarmService _alarmService;
    private readonly IDialogService _dialogService;
    private readonly DispatcherTimer _refreshTimer;

    public TimerListViewModel(
        TimerDefinitionsService defs,
        TimerProgressService progress,
        TimerAlarmService alarmService,
        IDialogService dialogService)
    {
        _defs = defs;
        _progress = progress;
        _alarmService = alarmService;
        _dialogService = dialogService;

        _defs.DefinitionsChanged += (_, _) => { SyncFromState(); RefreshAutocomplete(); };
        _progress.ProgressChanged += (_, _) => SyncFromState();

        TimersView = CollectionViewSource.GetDefaultView(Timers);
        TimersView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimerItemViewModel.GroupKey)));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.GroupKey), ListSortDirection.Ascending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.IsIdle), ListSortDirection.Descending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.IsDone), ListSortDirection.Ascending));

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => Tick();
        _refreshTimer.Start();

        SyncFromState();
        RefreshAutocomplete();
    }

    public ObservableCollection<TimerItemViewModel> Timers { get; } = [];
    public ICollectionView TimersView { get; }

    public ObservableCollection<string> KnownRegions { get; } = [];
    public ObservableCollection<string> KnownMaps { get; } = [];

    [RelayCommand]
    private void AddTimer()
    {
        var vm = new TimerDialogViewModel(existing: null, KnownRegions, KnownMaps);
        var content = new TimerDialogContent();

        if (_dialogService.ShowDialog(vm, content) != true) return;

        _defs.Add(new GandalfTimerDef
        {
            Name = vm.ResultName,
            Duration = vm.ResultDuration,
            Region = vm.ResultRegion,
            Map = vm.ResultMap,
        });
    }

    [RelayCommand]
    private void EditTimer(TimerItemViewModel? item)
    {
        if (item is null) return;

        var vm = new TimerDialogViewModel(item.View, KnownRegions, KnownMaps);
        var content = new TimerDialogContent();

        if (_dialogService.ShowDialog(vm, content) != true) return;

        var isIdleOnActive = item.State == TimerState.Idle;
        _defs.Update(item.Id, d =>
        {
            d.Name = vm.ResultName;
            d.Region = vm.ResultRegion;
            d.Map = vm.ResultMap;
            if (isIdleOnActive)
            {
                var duration = vm.ResultDuration;
                if (duration > TimeSpan.Zero)
                    d.Duration = duration;
            }
        });
    }

    [RelayCommand]
    private void StartTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        _progress.Start(vm.Id);
    }

    [RelayCommand]
    private void RestartTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        _progress.Restart(vm.Id);
    }

    [RelayCommand]
    private void DeleteTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        _defs.Remove(vm.Id);
    }

    [RelayCommand]
    private void CopyTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        var json = TimerClipboard.Serialize([vm.View.Def]);
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
            var def = TimerClipboard.ToDef(entry);
            if (def is not null) _defs.Add(def);
        }
    }

    [RelayCommand]
    private void SnoozeAll() => _alarmService.SnoozeAll();

    [RelayCommand]
    private void DismissAll() => _alarmService.DismissAll();

    private void SyncFromState()
    {
        Timers.Clear();
        foreach (var def in _defs.Definitions)
        {
            var progress = _progress.GetProgress(def.Id) ?? new TimerProgress();
            Timers.Add(new TimerItemViewModel(new TimerView(def, progress)));
        }
        TimersView.Refresh();
    }

    private void RefreshAutocomplete()
    {
        var regions = _defs.Definitions
            .Select(d => d.Region)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r);

        KnownRegions.Clear();
        foreach (var r in regions) KnownRegions.Add(r);

        var maps = _defs.Definitions
            .Select(d => d.Map)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m);

        KnownMaps.Clear();
        foreach (var m in maps) KnownMaps.Add(m);
    }

    private void Tick()
    {
        _progress.CheckExpirations();
        // Re-join with latest progress in case CheckExpirations stamped a CompletedAt.
        foreach (var vm in Timers)
        {
            var def = _defs.Definitions.FirstOrDefault(d => d.Id == vm.Id);
            if (def is null) continue;
            var progress = _progress.GetProgress(def.Id) ?? new TimerProgress();
            vm.UpdateView(new TimerView(def, progress));
        }
    }
}
