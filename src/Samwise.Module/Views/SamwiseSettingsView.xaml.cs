using System.Windows;
using Microsoft.Win32;
using Samwise.Alarms;

namespace Samwise.Views;

public partial class SamwiseSettingsView : System.Windows.Controls.UserControl
{
    public SamwiseSettingsView() { InitializeComponent(); }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not StageAlarmRule rule) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose an alarm sound",
            Filter = AlarmSoundPlayer.OpenFileFilter,
        };
        if (dlg.ShowDialog() == true) rule.SoundFilePath = dlg.FileName;
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is StageAlarmRule rule)
            AlarmSoundPlayer.Play(rule.SoundFilePath);
    }

    private void ClearSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is StageAlarmRule rule) rule.SoundFilePath = null;
    }
}
