using System.Windows;
using Mithril.Shared.Audio;
using Mithril.Shared.Settings;
using Microsoft.Win32;
using Samwise.Alarms;

namespace Samwise.Views;

public partial class SamwiseSettingsView : System.Windows.Controls.UserControl
{
    private IPlaybackHandle? _testHandle;

    public SamwiseSettingsView() { InitializeComponent(); }

    public AudioSettings? Audio { get; set; }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not StageAlarmRule rule) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose an alarm sound",
            Filter = AudioPlayer.OpenFileFilter,
        };
        if (dlg.ShowDialog() == true) rule.SoundFilePath = dlg.FileName;
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is StageAlarmRule rule
            && DataContext is SamwiseSettings s)
        {
            _testHandle?.Stop();
            _testHandle = AudioPlayer.Play(rule.SoundFilePath, (float)s.Alarms.AlarmVolume, "samwise");
        }
    }

    private void ClearSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is StageAlarmRule rule) rule.SoundFilePath = null;
    }
}
