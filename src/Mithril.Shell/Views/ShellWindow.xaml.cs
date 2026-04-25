using System;
using System.Drawing;

namespace Mithril.Shell.Views;

public partial class ShellWindow : System.Windows.Window
{
    public ShellWindow()
    {
        InitializeComponent();
        Tray.Icon = LoadTrayIcon();
        Tray.TrayLeftMouseDown += (_, __) =>
        {
            Show();
            WindowState = System.Windows.WindowState.Normal;
            Activate();
        };
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/mithril.ico", UriKind.Absolute));
            if (info?.Stream is { } s) using (s) return new Icon(s);
        }
        catch { }
        return SystemIcons.Application;
    }
}
