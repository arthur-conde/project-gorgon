using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Legolas.Controls;

internal static class ClickThrough
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public static void Apply(Window window, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        var desired = clickThrough
            ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED
            : style & ~WS_EX_TRANSPARENT;
        if (desired != style) SetWindowLong(hwnd, GWL_EXSTYLE, desired);
    }

    /// <summary>
    /// Forces the window to TOPMOST z-order without activating it. WPF's
    /// Topmost=true alone sometimes loses ranking when fullscreen-borderless
    /// games claim foreground; re-applying this on Loaded/Activated keeps the
    /// overlay above the game.
    /// </summary>
    public static void ForceTopmost(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Keeps an overlay reliably TOPMOST for its whole visible lifetime.
    /// <para><see cref="ForceTopmost"/> alone is wired only to
    /// <c>Loaded</c> (fires once) and <c>Activated</c> (fires only when the OS
    /// actually activates the window). The overlays are cached and driven by
    /// <c>Hide()</c>/<c>Show()</c>, and a <c>Show()</c> issued while the game
    /// owns the foreground does <b>not</b> activate the window — so the
    /// re-assert was being skipped and the window came back at ordinary
    /// z-order (behind the game). This re-asserts on every show
    /// (<see cref="UIElement.IsVisibleChanged"/> → visible) and, because a
    /// fullscreen-borderless game can reclaim z-order with no event at all,
    /// also on a low-frequency timer while the window is visible.</para>
    /// </summary>
    public static void KeepTopmost(Window window)
    {
        // Background priority + a coarse interval: one SetWindowPos every
        // couple seconds is negligible and 2 s is a fine worst-case recovery
        // window for the (rare) silent z-order steal.
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        timer.Tick += (_, _) => ForceTopmost(window);

        window.IsVisibleChanged += (_, _) =>
        {
            if (window.IsVisible)
            {
                ForceTopmost(window);
                timer.Start();
            }
            else
            {
                timer.Stop();
            }
        };
        window.Closed += (_, _) => timer.Stop();

        if (window.IsVisible)
        {
            ForceTopmost(window);
            timer.Start();
        }
    }
}
