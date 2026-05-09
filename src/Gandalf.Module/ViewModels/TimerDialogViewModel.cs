using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Gandalf.Domain;
using Mithril.Shared.Game;
using Mithril.Shared.Wpf.Dialogs;

namespace Gandalf.ViewModels;

public sealed partial class TimerDialogViewModel : DialogViewModelBase
{
    private readonly bool _isEditing;
    private readonly bool _areInputsEditable;

    [ObservableProperty] private string _name = "";

    [ObservableProperty] private bool _isCountdown = true;
    [ObservableProperty] private bool _isGameTimeOfDay;

    [ObservableProperty] private string _hours = "";
    [ObservableProperty] private string _minutes = "";
    [ObservableProperty] private string _seconds = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameTimePreview))]
    private string _gameHour = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GameTimePreview))]
    private string _gameMinute = "";

    [ObservableProperty] private bool _recurring;

    [ObservableProperty] private string _region = "";
    [ObservableProperty] private string _map = "";

    public ObservableCollection<string> KnownRegions { get; } = [];
    public ObservableCollection<string> KnownMaps { get; } = [];

    /// <summary>
    /// Trigger / duration / game-time inputs are gated to idle-only on the
    /// active character, matching the pre-existing rule for Duration: changing
    /// the firing semantics mid-run would reinterpret remaining time on every
    /// other character with in-flight progress for the same def.
    /// </summary>
    public bool AreInputsEditable => _areInputsEditable;

    /// <summary>12-hour preview that mirrors the shell's in-game-clock display.</summary>
    public string GameTimePreview
    {
        get
        {
            if (!int.TryParse(GameHour, out var h) || h < 0 || h > 23) return "";
            if (!int.TryParse(GameMinute, out var m) || m < 0 || m > 59) return "";
            return new GameTimeOfDay(h, m).ToString12Hour() + " in-game";
        }
    }

    public override string Title => _isEditing ? "Edit Timer" : "Add Timer";
    public override string PrimaryButtonText => _isEditing ? "Update" : "Save";

    partial void OnIsCountdownChanged(bool value)
    {
        if (value) IsGameTimeOfDay = false;
    }

    partial void OnIsGameTimeOfDayChanged(bool value)
    {
        if (value) IsCountdown = false;
    }

    public TimerDialogViewModel(
        GandalfTimerDef? existing,
        bool isIdleOnActive,
        IReadOnlyList<string> knownRegions,
        IReadOnlyList<string> knownMaps)
    {
        foreach (var r in knownRegions) KnownRegions.Add(r);
        foreach (var m in knownMaps) KnownMaps.Add(m);

        if (existing is not null)
        {
            _isEditing = true;
            _areInputsEditable = isIdleOnActive;
            _name = existing.Name;
            _region = existing.Region;
            _map = existing.Map;

            if (existing.Kind == GandalfTriggerKind.GameTimeOfDay)
            {
                _isCountdown = false;
                _isGameTimeOfDay = true;
                _gameHour = (existing.GameHour ?? 0).ToString();
                _gameMinute = (existing.GameMinute ?? 0).ToString("D2");
                _recurring = existing.Recurring;
            }
            else
            {
                _hours = existing.Duration.Hours > 0 || existing.Duration.Days > 0
                    ? ((int)existing.Duration.TotalHours).ToString() : "";
                _minutes = existing.Duration.Minutes > 0 ? existing.Duration.Minutes.ToString() : "";
                _seconds = existing.Duration.Seconds > 0 ? existing.Duration.Seconds.ToString() : "";
            }
        }
        else
        {
            _areInputsEditable = true;
        }
    }

    public override bool OnPrimaryAction()
    {
        // Edit mode skips re-validation: when the inputs are gated to idle and
        // the user can't change them, accept any state. When they can, the per-
        // kind validation below applies symmetrically with Add mode.
        if (_isEditing && !_areInputsEditable) return true;

        if (IsGameTimeOfDay)
        {
            if (!int.TryParse(GameHour, out var h) || h < 0 || h > 23) return false;
            if (!int.TryParse(GameMinute, out var m) || m < 0 || m > 59) return false;
            return true;
        }

        // Countdown: positive duration required.
        int.TryParse(Hours, out var hr);
        int.TryParse(Minutes, out var mi);
        int.TryParse(Seconds, out var se);
        return new TimeSpan(hr, mi, se) > TimeSpan.Zero;
    }

    public string ResultName => string.IsNullOrWhiteSpace(Name) ? "Timer" : Name.Trim();

    public GandalfTriggerKind ResultKind =>
        IsGameTimeOfDay ? GandalfTriggerKind.GameTimeOfDay : GandalfTriggerKind.Countdown;

    public TimeSpan ResultDuration
    {
        get
        {
            int.TryParse(Hours, out var h);
            int.TryParse(Minutes, out var m);
            int.TryParse(Seconds, out var s);
            return new TimeSpan(h, m, s);
        }
    }

    public int? ResultGameHour =>
        int.TryParse(GameHour, out var h) ? h : null;

    public int? ResultGameMinute =>
        int.TryParse(GameMinute, out var m) ? m : null;

    public bool ResultRecurring => Recurring;

    public string ResultRegion => Region.Trim();
    public string ResultMap => Map.Trim();

    partial void OnRegionChanged(string value)
    {
        KnownMaps.Clear();
        // Maps will be filtered by the caller if needed — keep it simple for now.
    }
}
