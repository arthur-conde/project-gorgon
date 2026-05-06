using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Legolas.Controls;

/// <summary>
/// Region-aware click-through for borderless overlay windows. Toggles
/// <c>WS_EX_TRANSPARENT</c> on a polling tick based on whether the
/// global cursor is over an "interactive chrome" region (header for
/// drag, resize border) versus the body.
///
/// Why polling and not <c>WM_NCHITTEST</c>: once <c>WS_EX_TRANSPARENT</c>
/// is set, Windows stops delivering mouse messages to the window —
/// including <c>WM_NCHITTEST</c> — so a hook can never clear the flag
/// after it's been set. Polling cursor position via <c>GetCursorPos</c>
/// works regardless of the extended style.
///
/// Install once via <see cref="Attach"/> and call <see cref="SetActive"/>
/// when the user toggles the click-through setting. The polling timer
/// stops automatically when the window closes.
/// </summary>
internal sealed class PartialClickThrough
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly Window _window;
    private readonly FrameworkElement[] _chromeRegions;
    private readonly double _resizeBorderThickness;
    private readonly DispatcherTimer _timer;
    private bool _active;

    private PartialClickThrough(Window window, FrameworkElement[] chromeRegions, double resizeBorderThickness)
    {
        _window = window;
        _chromeRegions = chromeRegions;
        _resizeBorderThickness = resizeBorderThickness;
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// Installs the polling controller on <paramref name="window"/>.
    /// <paramref name="resizeBorderThickness"/> is the band around the
    /// window edge that stays interactive for resize. <paramref name="chromeRegions"/>
    /// are additional rectangular elements that stay interactive
    /// (typically the drag header). Returns the controller; call
    /// <see cref="SetActive"/> to enable/disable.
    /// </summary>
    public static PartialClickThrough Attach(
        Window window,
        double resizeBorderThickness,
        params FrameworkElement[] chromeRegions)
    {
        var controller = new PartialClickThrough(window, chromeRegions, resizeBorderThickness);
        // Ensure the HWND exists so WindowInteropHelper(window).Handle is valid
        // for the SetWindowLong calls in OnTick.
        new WindowInteropHelper(window).EnsureHandle();
        window.Closed += (_, _) => controller._timer.Stop();
        return controller;
    }

    /// <summary>
    /// Enables (true) or disables (false) partial click-through. When
    /// disabled, <c>WS_EX_TRANSPARENT</c> is cleared so the window
    /// catches all clicks normally.
    /// </summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer.Stop();
            ApplyTransparent(false);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_active) return;
        if (!GetCursorPos(out var pt)) return;

        Point local;
        try { local = _window.PointFromScreen(new Point(pt.X, pt.Y)); }
        catch { return; }

        // Cursor outside the window? Leave the current flag state alone — doesn't
        // matter for this window since clicks aren't going to it anyway.
        if (local.X < 0 || local.Y < 0 ||
            local.X > _window.ActualWidth || local.Y > _window.ActualHeight)
        {
            return;
        }

        var overChrome = IsOverAnyChrome(local);
        // Body → click-through ON. Chrome → click-through OFF.
        ApplyTransparent(!overChrome);
    }

    private bool IsOverAnyChrome(Point localPoint)
    {
        // Resize band — within `_resizeBorderThickness` of any window edge.
        if (_resizeBorderThickness > 0 &&
            (localPoint.X <= _resizeBorderThickness ||
             localPoint.Y <= _resizeBorderThickness ||
             localPoint.X >= _window.ActualWidth - _resizeBorderThickness ||
             localPoint.Y >= _window.ActualHeight - _resizeBorderThickness))
        {
            return true;
        }

        foreach (var region in _chromeRegions)
        {
            if (region is null || !region.IsVisible) continue;
            if (region.ActualWidth <= 0 || region.ActualHeight <= 0) continue;
            var topLeft = region.TranslatePoint(new Point(0, 0), _window);
            if (localPoint.X >= topLeft.X &&
                localPoint.X <= topLeft.X + region.ActualWidth &&
                localPoint.Y >= topLeft.Y &&
                localPoint.Y <= topLeft.Y + region.ActualHeight)
            {
                return true;
            }
        }
        return false;
    }

    private void ApplyTransparent(bool enable)
    {
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        var hasFlag = (ex & WS_EX_TRANSPARENT) != 0;
        if (hasFlag == enable) return;
        var next = enable
            ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED
            : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, next);
    }
}
