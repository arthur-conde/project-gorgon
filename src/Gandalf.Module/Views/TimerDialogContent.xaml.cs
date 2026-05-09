using System.Windows;
using System.Windows.Controls;
using Gandalf.ViewModels;
using Microsoft.Win32;
using Mithril.Shared.Audio;

namespace Gandalf.Views;

public partial class TimerDialogContent : UserControl
{
    private IPlaybackHandle? _testHandle;

    public TimerDialogContent() => InitializeComponent();

    private TimerDialogViewModel? Vm => DataContext as TimerDialogViewModel;

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Choose an alarm sound for this timer",
            Filter = AudioPlayer.OpenFileFilter,
        };
        if (dlg.ShowDialog() == true) Vm.SoundFilePath = dlg.FileName;
    }

    private void TestSound_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        _testHandle?.Stop();
        // Per-timer path wins; null falls back to global default — same
        // selection rule TimerAlarmService.ResolveSoundPath uses at fire time.
        _testHandle = AudioPlayer.Play(Vm.SoundFilePath, volume: 1.0f, "gandalf");
    }

    private void ClearSound_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        Vm.SoundFilePath = null;
    }
}
