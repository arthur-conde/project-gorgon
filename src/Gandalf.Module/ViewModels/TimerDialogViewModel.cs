using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Reference;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf.Dialogs;

namespace Gandalf.ViewModels;

public sealed partial class TimerDialogViewModel : DialogViewModelBase
{
    /// <summary>
    /// Locked-down 12-hour culture for the picker — zero-padded
    /// <c>hh:mm tt</c>. Vanilla <c>en-US</c> uses <c>h:mm:ss tt</c>
    /// (single-digit hour, plus a stray seconds component the picker
    /// can't actually edit since we hide the seconds spinner).
    /// </summary>
    private static readonly CultureInfo TwelveHourCulture = BuildCulture("hh:mm tt");

    /// <summary>
    /// Zero-padded 24-hour culture for the picker. Built from the same
    /// <c>en-US</c> base so the date layout is unchanged — only the time
    /// pattern flips.
    /// </summary>
    private static readonly CultureInfo TwentyFourHourCulture = BuildCulture("HH:mm");

    private static CultureInfo BuildCulture(string timePattern)
    {
        var c = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
        c.DateTimeFormat.ShortTimePattern = timePattern;
        c.DateTimeFormat.LongTimePattern = timePattern;
        return c;
    }

    /// <summary>
    /// Picker culture for *this* dialog. Latched at construction from the
    /// user's <see cref="UserPreferences.Use24HourClock"/> preference. The
    /// dialog is short-lived; toggling the global preference while a
    /// dialog is open is an edge case we don't try to live-react to.
    /// </summary>
    public CultureInfo PickerCulture { get; }

    private readonly bool _isEditing;
    private readonly bool _areInputsEditable;

    [ObservableProperty] private string _name = "";

    [ObservableProperty] private bool _isCountdown = true;
    [ObservableProperty] private bool _isGameTimeOfDay;

    [ObservableProperty] private string _hours = "";
    [ObservableProperty] private string _minutes = "";
    [ObservableProperty] private string _seconds = "";

    /// <summary>
    /// Bound to the MahApps <c>TimePicker.SelectedDateTime</c>. The picker only
    /// exposes a <see cref="DateTime"/>-typed property — we ignore the date
    /// component and read <c>Hour</c>/<c>Minute</c>. Null means the user hasn't
    /// picked an in-game time yet (required when <see cref="IsGameTimeOfDay"/>
    /// is set). The picker enforces 0–23h / 0–59m structurally, so no
    /// per-component range validation is needed in <see cref="OnPrimaryAction"/>.
    /// </summary>
    [ObservableProperty] private DateTime? _selectedGameDateTime;
    [ObservableProperty] private bool _recurring;

    /// <summary>
    /// Free-form area label as displayed/typed by the user. Resolved at save time
    /// against <see cref="_areaLookup"/> to populate <see cref="ResultAreaKey"/>.
    /// </summary>
    [ObservableProperty] private string _area = "";

    /// <summary>
    /// Per-timer sound override. Null means "use the global default from
    /// <see cref="GandalfSettings.SoundFilePath"/>". Set via the Browse
    /// button in <c>TimerDialogContent.xaml.cs</c>; cleared via Reset.
    /// </summary>
    [ObservableProperty] private string? _soundFilePath;

    /// <summary>
    /// Suggestion pool for the Area combobox. Caller pre-populates with the union of
    /// areas.json FriendlyNames and the user's existing Area values.
    /// </summary>
    public ObservableCollection<string> KnownAreas { get; } = [];

    /// <summary>
    /// Case-insensitive FriendlyName → AreaEntry index used to canonicalize the
    /// typed area string at save time. Empty when the dialog is constructed without
    /// reference data (test scenarios) — in that case AreaKey will always be null.
    /// </summary>
    private readonly IReadOnlyDictionary<string, AreaEntry> _areaLookup;

    /// <summary>
    /// Trigger / duration / game-time inputs are gated to idle-only on the
    /// active character, matching the pre-existing rule for Duration: changing
    /// the firing semantics mid-run would reinterpret remaining time on every
    /// other character with in-flight progress for the same def.
    /// </summary>
    public bool AreInputsEditable => _areInputsEditable;

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
        IReadOnlyList<string> knownAreas,
        IReadOnlyDictionary<string, AreaEntry> areaLookup,
        UserPreferences preferences)
    {
        PickerCulture = preferences.Use24HourClock ? TwentyFourHourCulture : TwelveHourCulture;
        _areaLookup = areaLookup;
        foreach (var a in knownAreas) KnownAreas.Add(a);

        if (existing is not null)
        {
            _isEditing = true;
            _areInputsEditable = isIdleOnActive;
            _name = existing.Name;
            _area = existing.Area;
            _soundFilePath = existing.SoundFilePath;

            if (existing.Kind == GandalfTriggerKind.GameTimeOfDay)
            {
                _isCountdown = false;
                _isGameTimeOfDay = true;
                _selectedGameDateTime = DateTime.Today.AddHours(existing.GameHour ?? 0).AddMinutes(existing.GameMinute ?? 0);
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

        if (IsGameTimeOfDay) return SelectedGameDateTime is not null;

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

    public int? ResultGameHour => SelectedGameDateTime?.Hour;
    public int? ResultGameMinute => SelectedGameDateTime?.Minute;

    public bool ResultRecurring => Recurring;

    /// <summary>
    /// Save-time canonicalized area string. When the typed text matches a known
    /// FriendlyName from <c>areas.json</c> (case-insensitive), the canonical-cased
    /// FriendlyName is used so reopening the dialog displays the reference spelling.
    /// </summary>
    public string ResultArea => GandalfAreaResolver.Resolve(Area, _areaLookup).Area;

    /// <summary>
    /// Canonical PG area key (e.g. <c>"AreaSerbule"</c>) when the typed text resolves
    /// to a known area, else <c>null</c>. Side-channel — never shown to the user.
    /// </summary>
    public string? ResultAreaKey => GandalfAreaResolver.Resolve(Area, _areaLookup).AreaKey;

    public string? ResultSoundFilePath =>
        string.IsNullOrWhiteSpace(SoundFilePath) ? null : SoundFilePath.Trim();
}
