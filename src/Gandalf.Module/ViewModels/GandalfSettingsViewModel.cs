using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Game;
using Mithril.Shared.Settings;

namespace Gandalf.ViewModels;

/// <summary>
/// Per-shift binding row in the settings view. Wraps the static
/// <see cref="ShiftDefinition"/> (display data) with the live
/// <see cref="ShiftAlarmConfig"/> (Enabled + SoundFilePath, INPC).
/// The XAML <c>ItemsControl</c> binds to one of these per shift.
/// <see cref="Display"/> re-fires INPC when the user flips the
/// 12/24h preference, so the rows reformat live in the open settings panel.
/// </summary>
public sealed class ShiftAlarmRow : INotifyPropertyChanged
{
    private readonly UserPreferences _preferences;

    public ShiftAlarmRow(ShiftDefinition shift, ShiftAlarmConfig config, UserPreferences preferences)
    {
        Shift = shift;
        Config = config;
        _preferences = preferences;
        _preferences.PropertyChanged += OnPreferencesChanged;
    }

    public ShiftDefinition Shift { get; }
    public ShiftAlarmConfig Config { get; }

    public string Slug => Shift.Slug;
    public string Display
    {
        get
        {
            var formatted = new GameTimeOfDay(Shift.StartHour, 0).Format(_preferences.Use24HourClock);
            return $"{Shift.Emoji}  {Shift.Label}  ({formatted} in-game)";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UserPreferences.Use24HourClock))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
    }
}

/// <summary>
/// Settings view's binding target. Re-exposes <see cref="GandalfSettings"/> as
/// <see cref="Settings"/> so existing XAML bindings go through <c>Settings.AlarmEnabled</c>
/// etc., and adds the destructive <see cref="DeleteAllCommand"/>.
/// </summary>
public sealed partial class GandalfSettingsViewModel : ObservableObject
{
    private readonly TimerDefinitionsService _defs;
    private readonly TimerProgressService _progress;

    public GandalfSettingsViewModel(
        GandalfSettings settings,
        TimerDefinitionsService defs,
        TimerProgressService progress,
        GandalfShiftSettings shiftSettings,
        IShiftCatalog shiftCatalog,
        UserPreferences preferences)
    {
        Settings = settings;
        _defs = defs;
        _progress = progress;
        ShiftRows = shiftCatalog.Shifts
            .Select(s => new ShiftAlarmRow(s, shiftSettings.GetOrCreate(s.Slug), preferences))
            .ToArray();
    }

    public GandalfSettings Settings { get; }

    /// <summary>One row per published in-game-time-of-day shift, in time order.</summary>
    public IReadOnlyList<ShiftAlarmRow> ShiftRows { get; }

    [RelayCommand]
    private void DeleteAll()
    {
        var result = MessageBox.Show(
            "Delete every timer definition and clear every character's progress?\n\n" +
            "This cannot be undone.",
            "Delete All Timers",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        // Progress before defs so the in-memory view invalidates before the defs service
        // fires DefinitionsChanged (which triggers the list VM to rebuild and read progress).
        _progress.ClearAllProgressForAllCharacters();
        _defs.ClearAll();
    }
}
