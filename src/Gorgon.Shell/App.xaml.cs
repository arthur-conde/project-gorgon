using System.Windows;

namespace Gorgon.Shell;

public partial class App : System.Windows.Application
{
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activateCts;

    internal void Init(EventWaitHandle activateEvent, CancellationTokenSource activateCts)
    {
        _activateEvent = activateEvent;
        _activateCts = activateCts;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Pin Accessibility.dll early. WPF's Popup MSAA->UIA bridge JIT-loads it
        // whenever a tooltip opens with a UIA client listening, and the load is
        // not try/catched -- see dotnet/wpf#7751.
        _ = typeof(Accessibility.IAccessible).Assembly;

        DispatcherUnhandledException += (_, args) =>
        {
            Program.ShowFatal(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Program.ShowFatal(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Program.ShowFatal(args.Exception);
            args.SetObserved();
        };

        if (_activateEvent is not null && _activateCts is not null)
            _ = WatchActivateEvent(_activateEvent, _activateCts.Token);
    }

    private async Task WatchActivateEvent(EventWaitHandle ev, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var signaled = await Task.Run(() => ev.WaitOne(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            if (!signaled) continue;
            await Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is null) return;
                if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
                MainWindow.Show();
                MainWindow.Activate();
                MainWindow.Topmost = true;
                MainWindow.Topmost = false;
            });
        }
    }
}
