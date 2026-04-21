using System.Windows;
using Gandalf.Domain;
using Gorgon.Shared.Settings;
using Microsoft.Win32;
using Samwise.Alarms;

namespace Gandalf.Views;

public partial class GandalfSettingsView : System.Windows.Controls.UserControl
{
    private IPlaybackHandle? _testHandle;

    public GandalfSettingsView() { InitializeComponent(); }

    public AudioSettings? Audio { get; set; }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GandalfSettings settings) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose an alarm sound",
            Filter = AlarmSoundPlayer.OpenFileFilter,
        };
        if (dlg.ShowDialog() == true) settings.SoundFilePath = dlg.FileName;
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is GandalfSettings s)
        {
            _testHandle?.Stop();
            _testHandle = AlarmSoundPlayer.Play(s.SoundFilePath, (float)s.AlarmVolume, "gandalf");
        }
    }

    private void ClearSound_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is GandalfSettings s) s.SoundFilePath = null;
    }
}
