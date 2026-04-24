using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gandalf.Domain;
using Gandalf.Services;

namespace Gandalf.ViewModels;

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
        TimerProgressService progress)
    {
        Settings = settings;
        _defs = defs;
        _progress = progress;
    }

    public GandalfSettings Settings { get; }

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
