using System.Windows;
using Gandalf.Domain;
using Gandalf.ViewModels;
using Mithril.Shared.Audio;
using Microsoft.Win32;

namespace Gandalf.Views;

public partial class GandalfSettingsView : System.Windows.Controls.UserControl
{
    private IPlaybackHandle? _testHandle;

    public GandalfSettingsView() { InitializeComponent(); }

    private GandalfSettings? CurrentSettings => (DataContext as GandalfSettingsViewModel)?.Settings;

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var settings = CurrentSettings;
        if (settings is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose an alarm sound",
            Filter = AudioPlayer.OpenFileFilter,
        };
        if (dlg.ShowDialog() == true) settings.SoundFilePath = dlg.FileName;
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentSettings;
        if (s is null) return;
        _testHandle?.Stop();
        _testHandle = AudioPlayer.Play(s.SoundFilePath, (float)s.AlarmVolume, "gandalf");
    }

    private void ClearSound_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentSettings;
        if (s is not null) s.SoundFilePath = null;
    }

    private static Gandalf.ViewModels.ShiftAlarmRow? RowFor(object sender) =>
        (sender as System.Windows.FrameworkElement)?.DataContext
            as Gandalf.ViewModels.ShiftAlarmRow;

    private void BrowseShift_Click(object sender, RoutedEventArgs e)
    {
        if (RowFor(sender) is not { } row) return;
        var dlg = new OpenFileDialog
        {
            Title = $"Choose an alarm sound for {row.Shift.Label}",
            Filter = AudioPlayer.OpenFileFilter,
        };
        if (dlg.ShowDialog() == true) row.Config.SoundFilePath = dlg.FileName;
    }

    private void TestShift_Click(object sender, RoutedEventArgs e)
    {
        if (RowFor(sender) is not { } row) return;
        var s = CurrentSettings;
        var path = row.Config.SoundFilePath ?? s?.SoundFilePath;
        var volume = (float?)s?.AlarmVolume ?? 1.0f;
        _testHandle?.Stop();
        _testHandle = AudioPlayer.Play(path, volume, "gandalf");
    }

    private void ClearShift_Click(object sender, RoutedEventArgs e)
    {
        if (RowFor(sender) is { } row) row.Config.SoundFilePath = null;
    }
}
