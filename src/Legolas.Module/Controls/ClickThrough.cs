using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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
}
