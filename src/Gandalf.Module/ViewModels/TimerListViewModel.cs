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
    private readonly ITimerSource _source;
    private readonly TimerDefinitionsService? _defs;
    private readonly TimerProgressService? _progress;
    private readonly TimerAlarmService? _alarmService;
    private readonly IDialogService? _dialogService;
    private readonly IActiveCharacterService? _active;
    private readonly ICharacterPresenceService? _presence;
    private readonly DispatcherTimer _refreshTimer;

    public TimerListViewModel(
        ITimerSource source,
        TimerDefinitionsService? defs = null,
        TimerProgressService? progress = null,
        TimerAlarmService? alarmService = null,
        IDialogService? dialogService = null,
        IActiveCharacterService? active = null,
        ICharacterPresenceService? presence = null)
    {
        _source = source;
        _defs = defs;
        _progress = progress;
        _alarmService = alarmService;
        _dialogService = dialogService;
        _active = active;
        _presence = presence;

        _source.CatalogChanged += (_, _) => { SyncFromState(); RefreshAutocomplete(); };
        _source.ProgressChanged += (_, _) => SyncFromState();

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
        if (_defs is null || _dialogService is null) return;
        var vm = new TimerDialogViewModel(existing: null, isIdleOnActive: true, KnownRegions, KnownMaps);
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
        if (item is null || _defs is null || _dialogService is null) return;
        if (item.Catalog.SourceMetadata is not GandalfTimerDef existing) return;

        var isIdleOnActive = item.State == TimerState.Idle;
        var vm = new TimerDialogViewModel(existing, isIdleOnActive, KnownRegions, KnownMaps);
        var content = new TimerDialogContent();

        if (_dialogService.ShowDialog(vm, content) != true) return;
        _defs.Update(item.Key, d =>
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
        if (vm is null || _progress is null) return;
        vm.ElapsedWhileAway = false;
        _progress.Start(vm.Key);
    }

    [RelayCommand]
    private void RestartTimer(TimerItemViewModel? vm)
    {
        if (vm is null || _progress is null) return;
        vm.ElapsedWhileAway = false;
        _progress.Restart(vm.Key);
    }

    [RelayCommand]
    private void DeleteTimer(TimerItemViewModel? vm)
    {
        if (vm is null || _defs is null) return;
        vm.ElapsedWhileAway = false;
        _defs.Remove(vm.Key);
    }

    [RelayCommand]
    private void ClearDone()
    {
        _progress?.ClearAllDoneOnActive();
    }

    [RelayCommand]
    private void CopyTimer(TimerItemViewModel? vm)
    {
        if (vm is null) return;
        if (vm.Catalog.SourceMetadata is not GandalfTimerDef def) return;
        var json = TimerClipboard.Serialize([def]);
        try { Clipboard.SetText(json); } catch { }
    }

    [RelayCommand]
    private void PasteTimers()
    {
        if (_defs is null) return;
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
    private void SnoozeAll() => _alarmService?.SnoozeAll();

    [RelayCommand]
    private void DismissAll() => _alarmService?.DismissAll();

    private void SyncFromState()
    {
        Timers.Clear();
        var progress = _source.Progress;
        foreach (var entry in _source.Catalog)
        {
            progress.TryGetValue(entry.Key, out var p);
            Timers.Add(new TimerItemViewModel(new TimerRow(entry, p)));
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
        if (_active is null || _presence is null) return;
        var name = _active.ActiveCharacterName;
        var server = _active.ActiveServer;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(server)) return;

        var lastActive = _presence.GetLastActiveAt(name, server);
        if (lastActive is null) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var vm in Timers)
        {
            if (vm.Row.Progress is null) continue;
            // Bridge the source-shape progress into the classifier's TimerProgress shape.
            var progress = new TimerProgress { StartedAt = vm.Row.Progress.StartedAt };
            if (ElapsedWhileAwayClassifier.IsElapsedWhileAway(progress, vm.Row.Duration, lastActive, now))
                vm.ElapsedWhileAway = true;
        }
    }

    private void RefreshAutocomplete()
    {
        // Autocomplete still reads the user-specific def list (Region+Map are user-only fields).
        if (_defs is null) return;

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
        _progress?.CheckExpirations();
        // Re-join with latest progress in case CheckExpirations stamped a CompletedAt.
        var progress = _source.Progress;
        var catalog = _source.Catalog.ToDictionary(c => c.Key, StringComparer.Ordinal);
        foreach (var vm in Timers)
        {
            if (!catalog.TryGetValue(vm.Key, out var entry)) continue;
            progress.TryGetValue(vm.Key, out var p);
            vm.UpdateRow(new TimerRow(entry, p));
        }
    }
}
