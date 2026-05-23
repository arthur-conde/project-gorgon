using System.Runtime.CompilerServices;
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
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

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

    public static void Apply(Window window, bool clickThrough) =>
        Apply(window, clickThrough, interactiveRegion: null);

    /// <summary>
    /// Apply click-through to <paramref name="window"/>, optionally carving out
    /// an <paramref name="interactiveRegion"/> that stays clickable while the
    /// rest of the window passes clicks to whatever sits behind it.
    ///
    /// <para>Two modes:</para>
    /// <list type="bullet">
    /// <item><b>Full-window click-through</b> (<paramref name="interactiveRegion"/>
    /// null): toggles the legacy <c>WS_EX_TRANSPARENT</c> flag — every pixel
    /// passes clicks through. The inventory overlay relies on this.</item>
    /// <item><b>Partial click-through</b> (region supplied): leaves
    /// <c>WS_EX_TRANSPARENT</c> off and installs a <c>WM_NCHITTEST</c> hook
    /// that returns <c>HTTRANSPARENT</c> for points outside the region's live
    /// screen bounds. The map overlay uses this so its header chrome (drag
    /// handle, #520 nudge-pad toggle) stays reachable while the body still
    /// passes clicks to the game — covers both the user's ClickThroughMap
    /// preference and the calibration Drop phase's forced-on click-through.
    /// The two paths are mutually exclusive: <c>WS_EX_TRANSPARENT</c> would
    /// win over <c>HTTRANSPARENT</c> at the OS hit-test level, so the partial
    /// path explicitly clears it. The hook re-queries the region's live
    /// bounds each call, so window-resize / chrome-content changes Just Work
    /// without re-applying.</item>
    /// </list>
    /// </summary>
    public static void Apply(Window window, bool clickThrough, FrameworkElement? interactiveRegion)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        var state = _hookState.GetValue(window, _ => new HookState());
        state.ClickThrough = clickThrough;
        state.Region = interactiveRegion;

        var partial = clickThrough && interactiveRegion is not null;

        // WS_EX_TRANSPARENT is global to the window. Use it ONLY for the
        // legacy full-window mode; the partial mode relies on WM_NCHITTEST
        // and would be defeated by WS_EX_TRANSPARENT.
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        var desired = (clickThrough && !partial)
            ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED
            : style & ~WS_EX_TRANSPARENT;
        if (desired != style) SetWindowLong(hwnd, GWL_EXSTYLE, desired);

        // Install the hook lazily on first partial Apply. It stays installed
        // for the window's lifetime (HwndSource lifetime); when ClickThrough
        // is off or Region is null, the hook short-circuits to a no-op, so a
        // window that toggles between modes doesn't churn hooks.
        if (state.Hook is null && partial)
        {
            var source = HwndSource.FromHwnd(hwnd);
            if (source is null) return;
            state.Hook = (IntPtr _, int msg, IntPtr _, IntPtr lParam, ref bool handled) =>
            {
                if (msg != WM_NCHITTEST || !state.ClickThrough || state.Region is not { } region)
                    return IntPtr.Zero;
                // lParam packs screen X (low word) and Y (high word) as
                // signed shorts — sign-extend so multi-monitor setups with
                // negative screen coords still resolve correctly.
                var raw = lParam.ToInt64();
                var physX = (short)(raw & 0xFFFF);
                var physY = (short)((raw >> 16) & 0xFFFF);
                try
                {
                    // WM_NCHITTEST is in physical pixels; WPF's
                    // PointFromScreen expects DIP. Route through the
                    // composition target's device transform first (no-op at
                    // 100% DPI, correct at every per-monitor scale).
                    if (PresentationSource.FromVisual(region) is not HwndSource regionSource
                        || regionSource.CompositionTarget is null)
                        return IntPtr.Zero;
                    var dip = regionSource.CompositionTarget.TransformFromDevice
                        .Transform(new Point(physX, physY));
                    var local = region.PointFromScreen(dip);
                    if (local.X >= 0 && local.X < region.ActualWidth &&
                        local.Y >= 0 && local.Y < region.ActualHeight)
                    {
                        // Inside the interactive region — let WPF's normal
                        // hit-test win.
                        return IntPtr.Zero;
                    }
                }
                catch
                {
                    // Mid-resize / pre-arrange / detached visual — fail safe
                    // to "interactive", so a transient layout glitch can't
                    // strand the user (the toggle stays reachable instead of
                    // disappearing into a black hole).
                    return IntPtr.Zero;
                }
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            };
            source.AddHook(state.Hook);
        }
    }

    private sealed class HookState
    {
        public bool ClickThrough;
        public FrameworkElement? Region;
        public HwndSourceHook? Hook;
    }

    // Keyed on Window so per-window state lives exactly as long as the window
    // — no manual disposal, no static leak across module reloads.
    private static readonly ConditionalWeakTable<Window, HookState> _hookState = new();

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
