using System.Drawing;

namespace Gorgon.Shell.Views;

public partial class ShellWindow : System.Windows.Window
{
    public ShellWindow()
    {
        InitializeComponent();
        // Use a built-in Windows icon so we don't need to ship a .ico asset.
        Tray.Icon = SystemIcons.Application;
        Tray.TrayLeftMouseDown += (_, __) =>
        {
            Show();
            WindowState = System.Windows.WindowState.Normal;
            Activate();
        };
    }
}
