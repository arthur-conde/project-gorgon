using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gandalf.Domain;
using Gandalf.Services;
using Gandalf.Views;
using Mithril.Shared.Character;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf.Dialogs;

namespace Gandalf.ViewModels;

public sealed partial class TimerListViewModel : ObservableObject, IDisposable
{
    private readonly ITimerSource _source;
    private readonly TimerDefinitionsService? _defs;
    private readonly TimerProgressService? _progress;
    private readonly TimerAlarmService? _alarmService;
    private readonly IDialogService? _dialogService;
    private readonly IActiveCharacterService? _active;
    private readonly ICharacterPresenceService? _presence;
    private readonly UserPreferences? _preferences;
    private readonly IReferenceDataService? _refData;
    private readonly TimeProvider _time;
    private readonly TimerDisplayScheduler _scheduler;
    private readonly TimerSourceBinder _binder;

    /// <summary>
    /// Cached case-insensitive FriendlyName → AreaEntry index from
    /// <see cref="IReferenceDataService.Areas"/>. Built once on construction; used by
    /// the dialog to resolve typed text → canonical AreaKey at save time, and by
    /// <see cref="RefreshAutocomplete"/> to seed the suggestion pool. Empty when
    /// <c>_refData</c> is null (test scenarios).
    /// </summary>
    private IReadOnlyDictionary<string, AreaEntry> _areaLookup =
        new Dictionary<string, AreaEntry>(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public TimerListViewModel(
        ITimerSource source,
        TimerDefinitionsService? defs = null,
        TimerProgressService? progress = null,
        TimerAlarmService? alarmService = null,
        IDialogService? dialogService = null,
        IActiveCharacterService? active = null,
        ICharacterPresenceService? presence = null,
        UserPreferences? preferences = null,
        IReferenceDataService? refData = null,
        TimeProvider? time = null)
    {
        _source = source;
        _defs = defs;
        _progress = progress;
        _alarmService = alarmService;
        _dialogService = dialogService;
        _active = active;
        _presence = presence;
        _preferences = preferences;
        _refData = refData;
        if (_refData is not null) _areaLookup = GandalfAreaResolver.BuildLookup(_refData);
        _time = time ?? TimeProvider.System;

        TimersView = CollectionViewSource.GetDefaultView(Timers);
        TimersView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimerItemViewModel.GroupKey)));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.GroupKey), ListSortDirection.Ascending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.IsIdle), ListSortDirection.Descending));
        TimersView.SortDescriptions.Add(new SortDescription(nameof(TimerItemViewModel.IsDone), ListSortDirection.Ascending));

        _scheduler = new TimerDisplayScheduler(_time);
        _binder = new TimerSourceBinder(_source, Timers, _time, scheduler: _scheduler);

        _binder.RefreshRequired += OnBinderRefreshRequired;
        _scheduler.RefreshRequired += (_, _) => TimersView.Refresh();

        // ElapsedWhileAway depends on (Active character × LastActiveAt × row
        // progress). Recompute on character switch — character export
        // refresh isn't relevant, only "we're now looking at a different
        // character." The binder will replay rows for the new character via
        // the PerCharacterView swap fired from TimerProgressService, so
        // re-applying after the active-character change naturally aligns.
        if (_active is not null) _active.ActiveCharacterChanged += OnActiveCharacterChanged;

        ApplyElapsedWhileAwayBadges();
        RefreshAutocomplete();
    }

    public ObservableCollection<TimerItemViewModel> Timers { get; } = [];
    public ICollectionView TimersView { get; }

    /// <summary>
    /// Autocomplete pool for the timer dialog's "Area" field. Union of canonical
    /// PG area FriendlyNames (from <c>areas.json</c>) and the user's distinct
    /// existing <see cref="GandalfTimerDef.Area"/> values. Sorted, case-insensitive
    /// dedup. Refreshed on def-list mutations via <see cref="RefreshAutocomplete"/>.
    /// </summary>
    public ObservableCollection<string> KnownAreas { get; } = [];

    [RelayCommand]
    private void AddTimer()
    {
        if (_defs is null || _dialogService is null) return;
        var vm = new TimerDialogViewModel(existing: null, isIdleOnActive: true, KnownAreas, _areaLookup,
            _preferences ?? new UserPreferences());
        var content = new TimerDialogContent();

        if (_dialogService.ShowDialog(vm, content) != true) return;

        _defs.Add(new GandalfTimerDef
        {
            Name = vm.ResultName,
            Kind = vm.ResultKind,
            Duration = vm.ResultDuration,
            GameHour = vm.ResultGameHour,
            GameMinute = vm.ResultGameMinute,
            Recurring = vm.ResultRecurring,
            Area = vm.ResultArea,
            AreaKey = vm.ResultAreaKey,
            SoundFilePath = vm.ResultSoundFilePath,
        });
    }

    [RelayCommand]
    private void EditTimer(TimerItemViewModel? item)
    {
        if (item is null || _defs is null || _dialogService is null) return;
        if (item.Catalog.SourceMetadata is not GandalfTimerDef existing) return;

        var isIdleOnActive = item.State == TimerState.Idle;
        var vm = new TimerDialogViewModel(existing, isIdleOnActive, KnownAreas, _areaLookup,
            _preferences ?? new UserPreferences());
        var content = new TimerDialogContent();

        if (_dialogService.ShowDialog(vm, content) != true) return;
        _defs.Update(item.Key, d =>
        {
            d.Name = vm.ResultName;
            d.Area = vm.ResultArea;
            d.AreaKey = vm.ResultAreaKey;
            d.SoundFilePath = vm.ResultSoundFilePath;
            if (isIdleOnActive)
            {
                d.Kind = vm.ResultKind;
                d.Recurring = vm.ResultRecurring;
                if (vm.ResultKind == GandalfTriggerKind.GameTimeOfDay)
                {
                    d.GameHour = vm.ResultGameHour;
                    d.GameMinute = vm.ResultGameMinute;
                }
                else
                {
                    var duration = vm.ResultDuration;
                    if (duration > TimeSpan.Zero)
                        d.Duration = duration;
                }
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

    private void OnBinderRefreshRequired(object? sender, EventArgs e)
    {
        // Catalog mutations may have introduced new defs; refresh the
        // autocomplete suggestions so the next dialog open sees them.
        RefreshAutocomplete();
        TimersView.Refresh();
    }

    private void OnActiveCharacterChanged(object? sender, EventArgs e) =>
        ApplyElapsedWhileAwayBadges();

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

        var now = _time.GetUtcNow();
        foreach (var vm in Timers)
        {
            if (vm.Row.Progress is null) continue;
            if (vm.Row.FiringAt is not { } firingAt) continue;
            // Bridge the source-shape progress into the classifier's TimerProgress shape.
            var progress = new TimerProgress { StartedAt = vm.Row.Progress.StartedAt };
            if (ElapsedWhileAwayClassifier.IsElapsedWhileAway(progress, firingAt, lastActive, now))
                vm.ElapsedWhileAway = true;
        }
    }

    private void RefreshAutocomplete()
    {
        // Pool = (areas.json FriendlyNames) ∪ (distinct user-entered Area values),
        // case-insensitive dedup, sorted alphabetically. Reference data covers PG's
        // canonical regions; the user pool covers sub-locations and freeform labels
        // ("Hogan's Keep", "My Hideout") that aren't in areas.json.
        if (_defs is null) return;

        var pool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _areaLookup.Values)
        {
            if (!string.IsNullOrWhiteSpace(entry.FriendlyName))
                pool.Add(entry.FriendlyName);
        }
        foreach (var d in _defs.Definitions)
        {
            if (!string.IsNullOrWhiteSpace(d.Area))
                pool.Add(d.Area);
        }

        KnownAreas.Clear();
        foreach (var a in pool.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            KnownAreas.Add(a);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_active is not null) _active.ActiveCharacterChanged -= OnActiveCharacterChanged;
        _binder.RefreshRequired -= OnBinderRefreshRequired;
        _binder.Dispose();
        _scheduler.Dispose();
    }
}
