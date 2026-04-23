using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Gorgon.Shell.ViewModels;

public sealed partial class AppearanceSettingsViewModel : ObservableObject
{
    public ShellSettings Settings { get; }

    public AppearanceSettingsViewModel(ShellSettings settings)
    {
        Settings = settings;
        AvailableFonts = new ObservableCollection<string>(
            Fonts.SystemFontFamilies
                .Select(f => f.Source)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }

    public ObservableCollection<string> AvailableFonts { get; }

    public double MinFontSize => 9.0;
    public double MaxFontSize => 20.0;
}
