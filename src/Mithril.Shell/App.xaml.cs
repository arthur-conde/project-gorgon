using System.IO;
using System.Windows;
using Mithril.Shared.Modules;

namespace Mithril.Shell;

public partial class App : System.Windows.Application
{
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _activateCts;

    internal void Init(EventWaitHandle activateEvent, CancellationTokenSource activateCts)
    {
        _activateEvent = activateEvent;
        _activateCts = activateCts;
    }

    /// <summary>Set by <see cref="Program"/> after the host is built, so the activation-event
    /// loop can dispatch <c>mithril://</c> URIs forwarded from a second instance.</summary>
    public IDeepLinkRouter? DeepLinkRouter { get; set; }

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

            // The second instance may have dropped off a mithril:// URI before signalling us.
            // Read-and-delete in one shot; missing / unreadable file is a no-op.
            var activationUri = TryConsumeActivationUri();

            await Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow is null) return;
                if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
                MainWindow.Show();
                MainWindow.Activate();
                MainWindow.Topmost = true;
                MainWindow.Topmost = false;

                if (activationUri is not null) DeepLinkRouter?.Handle(activationUri);
            });
        }
    }

    private static string? TryConsumeActivationUri()
    {
        try
        {
            var path = Program.ActivationUriPath;
            if (!File.Exists(path)) return null;
            var uri = File.ReadAllText(path).Trim();
            try { File.Delete(path); } catch { /* best-effort */ }
            return string.IsNullOrEmpty(uri) ? null : uri;
        }
        catch
        {
            return null;
        }
    }
}
