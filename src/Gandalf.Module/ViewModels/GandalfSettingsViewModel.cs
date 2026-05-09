using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gandalf.Domain;
using Gandalf.Services;
using Mithril.Shared.Game;

namespace Gandalf.ViewModels;

/// <summary>
/// Per-shift binding row in the settings view. Wraps the static
/// <see cref="ShiftDefinition"/> (display data) with the live
/// <see cref="ShiftAlarmConfig"/> (Enabled + SoundFilePath, INPC).
/// The XAML <c>ItemsControl</c> binds to one of these per shift.
/// </summary>
public sealed class ShiftAlarmRow
{
    public ShiftAlarmRow(ShiftDefinition shift, ShiftAlarmConfig config)
    {
        Shift = shift;
        Config = config;
    }

    public ShiftDefinition Shift { get; }
    public ShiftAlarmConfig Config { get; }

    public string Slug => Shift.Slug;
    public string Display => $"{Shift.Emoji}  {Shift.Label}  ({Shift.StartHour}:00 in-game)";
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
        GandalfShiftSettings shiftSettings)
    {
        Settings = settings;
        _defs = defs;
        _progress = progress;
        ShiftRows = TimeOfDayShifts.All
            .Select(s => new ShiftAlarmRow(s, shiftSettings.GetOrCreate(s.Slug)))
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
