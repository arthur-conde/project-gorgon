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
using Mithril.Shared.Character;
using Mithril.Shared.Wpf.Dialogs;

namespace Gandalf.ViewModels;

public sealed partial class TimerListViewModel : ObservableObject
{
    private readonly TimerDefinitionsService _defs;
    private readonly TimerProgressService _progress;
    private readonly TimerAlarmService _alarmService;
    private readonly IDialogService _dialogService;
    private readonly IActiveCharacterService _active;
    private readonly ICharacterPresenceService _presence;
    private readonly DispatcherTimer _refreshTimer;

    public TimerListViewModel(
        TimerDefinitionsService defs,
        TimerProgressService progress,
        TimerAlarmService alarmService,
        IDialogService dialogService,
        IActiveCharacterService active,
        ICharacterPresenceService presence)
    {
        _defs = defs;
        _progress = progress;
        _alarmService = alarmService;
        _dialogService = dialogService;
        _active = active;
        _presence = presence;

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
        vm.ElapsedWhileAway = false;
        _progress.Start(vm.Id);
    }

    [RelayCommand]
    private void RestartTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        vm.ElapsedWhileAway = false;
        _progress.Restart(vm.Id);
    }

    [RelayCommand]
    private void DeleteTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        vm.ElapsedWhileAway = false;
        _defs.Remove(vm.Id);
    }

    [RelayCommand]
    private void ClearDone() => _progress.ClearAllDoneOnActive();

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

        ApplyElapsedWhileAwayBadges();
        TimersView.Refresh();
    }

    /// <summary>
    /// Mark timers whose theoretical completion (StartedAt + Duration) fell between the
    /// character's last-active stamp and now — these finished while the user was on another
    /// character. Uses theoretical completion (not CompletedAt) so the decision is
    /// independent of whether CheckExpirations has ticked on the newly-active character yet.
    /// Skipped when LastActiveAt is null (first-ever session on this character).
    /// </summary>
    private void ApplyElapsedWhileAwayBadges()
    {
        var name = _active.ActiveCharacterName;
        var server = _active.ActiveServer;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(server)) return;

        var lastActive = _presence.GetLastActiveAt(name, server);
        if (lastActive is null) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var vm in Timers)
        {
            if (ElapsedWhileAwayClassifier.IsElapsedWhileAway(
                vm.View.Progress, vm.View.Def.Duration, lastActive, now))
            {
                vm.ElapsedWhileAway = true;
            }
        }
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
