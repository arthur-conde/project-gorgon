using System;
using System.Windows;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Pure, testable rect math for the <see cref="RegionSnipWindow"/> selector
/// (#940, spec §7). Extracted from the live-drag UI so the coordinate logic can
/// be unit-tested headlessly; the drag interaction itself is manual-verify.
/// </summary>
internal static class SnipRectMath
{
    /// <summary>
    /// Normalise a drag (anchor → cursor) into a positive-extent rect, regardless
    /// of drag direction (up-left, down-right, etc.).
    /// </summary>
    public static Rect Normalize(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// Translate a selection expressed in the snip canvas's local DIUs (origin at
    /// the canvas top-left, which sits at the virtual-screen origin) into absolute
    /// virtual-desktop DIUs — the frame WPF <c>Window.Left/Top</c> use. The snip
    /// window is positioned at (<paramref name="virtualLeft"/>,
    /// <paramref name="virtualTop"/>), so the absolute rect is the local rect plus
    /// that offset. This is the SAME frame the overlay window's bounds use, so the
    /// result maps onto the overlay's Left/Top/Width/Height with no further
    /// transform (one-rect model).
    /// </summary>
    public static Rect ToVirtualDesktop(Rect local, double virtualLeft, double virtualTop) =>
        new(local.X + virtualLeft, local.Y + virtualTop, local.Width, local.Height);
}
