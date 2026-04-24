using System.Windows;
using Gandalf.Domain;
using Gandalf.ViewModels;
using Gorgon.Shared.Settings;
using Microsoft.Win32;
using Samwise.Alarms;

namespace Gandalf.Views;

public partial class GandalfSettingsView : System.Windows.Controls.UserControl
{
    private IPlaybackHandle? _testHandle;

    public GandalfSettingsView() { InitializeComponent(); }

    public AudioSettings? Audio { get; set; }

    private GandalfSettings? CurrentSettings => (DataContext as GandalfSettingsViewModel)?.Settings;

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var settings = CurrentSettings;
        if (settings is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose an alarm sound",
            Filter = AlarmSoundPlayer.OpenFileFilter,
        };
        if (dlg.ShowDialog() == true) settings.SoundFilePath = dlg.FileName;
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentSettings;
        if (s is null) return;
        _testHandle?.Stop();
        _testHandle = AlarmSoundPlayer.Play(s.SoundFilePath, (float)s.AlarmVolume, "gandalf");
    }

    private void ClearSound_Click(object sender, RoutedEventArgs e)
    {
        var s = CurrentSettings;
        if (s is not null) s.SoundFilePath = null;
    }
}
