using System.Linq;
using System.Windows;
using Mithril.Shared.Audio;
using Microsoft.Win32;
using Samwise.Alarms;
using Samwise.State;

namespace Samwise.Views;

public partial class SamwiseSettingsView : System.Windows.Controls.UserControl
{
    public SamwiseSettingsView() { InitializeComponent(); }

    public AlarmService? Alarms { get; set; }

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
        if (sender is FrameworkElement fe && fe.Tag is PlotStage stage)
            Alarms?.PreviewStage(stage);
    }

    private void StopPreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is PlotStage stage)
            Alarms?.StopPreview(stage);
    }

    private void StopAllSounds_Click(object sender, RoutedEventArgs e) => Alarms?.StopAllPlayback();

    private void ClearSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is StageAlarmRule rule) rule.SoundFilePath = null;
    }

    private void AddChannel_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SamwiseSettings s) return;
        var next = $"Channel {s.Alarms.Channels.Count + 1}";
        s.Alarms.Channels.Add(new AlarmChannel { Name = next, Collision = AlarmCollisionBehavior.Mix });
        // Bound ItemsControl picks up the change because Channels is a List<T>
        // and AlarmSettings raises PropertyChanged(nameof(Channels)) via the
        // attached channel-event fan-out. If the list-mutation doesn't refresh
        // the ItemsControl in practice, swap List<T> -> ObservableCollection<T>
        // - but defer that until we see the issue.
    }

    private void DeleteChannel_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SamwiseSettings s) return;
        if (sender is not FrameworkElement fe || fe.Tag is not AlarmChannel channel) return;
        if (s.Alarms.Channels.Count <= 1) return; // never delete the last channel

        var fallbackId = s.Alarms.Channels.First(c => c.Id != channel.Id).Id;
        foreach (var rule in s.Alarms.Rules.Values)
        {
            if (rule.ChannelId == channel.Id)
                rule.ChannelId = fallbackId;
        }
        s.Alarms.Channels.Remove(channel);
    }
}
