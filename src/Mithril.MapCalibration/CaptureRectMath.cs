using System;

namespace Mithril.MapCalibration;

/// <summary>
/// Pure, WPF-free conversions between device-independent (DIU/WPF) coordinates and
/// the physical-pixel <see cref="CaptureRect"/> that <c>BitBltScreenCapture</c> reads.
///
/// <para><b>Why this exists.</b> <c>BitBltScreenCapture.Capture</c> blits from
/// <c>GetDC(NULL)</c> (the whole virtual desktop) using <c>rect.X/Y/Width/Height</c>
/// as <i>physical pixels</i>, origin at the primary monitor, with negative coords
/// possible on a multi-monitor layout where a secondary screen sits left/above the
/// primary. WPF reports window geometry in DIUs (1 DIU = 1/96") relative to the
/// same virtual-desktop origin. So the capture rect is just the window's DIU rect
/// scaled by the per-monitor DPI factor — there is NO capture↔overlay transform,
/// the overlay frame <i>is</i> the capture frame (spec §7, #940 one-rect model).</para>
///
/// <para><b>Exactness contract.</b> The result must match what BitBlt grabs
/// exactly; this is the decode/DPI bug class that cost the #897 gate study two
/// false dead-ends. The DIU origin is signed (negative on a left/top secondary
/// monitor) and survives the scale, so the physical origin stays signed too. We
/// round to the nearest pixel (not truncate) so a sub-pixel DIU edge doesn't drop
/// a column/row, and floor/ceil the extent via edge rounding so width/height never
/// collapse below the visible region.</para>
/// </summary>
public static class CaptureRectMath
{
    /// <summary>
    /// Convert a window's device-independent rect (WPF <c>Left/Top/ActualWidth/
    /// ActualHeight</c>) to the physical-pixel desktop rect BitBlt reads.
    /// </summary>
    /// <param name="diuLeft">Window left edge, DIUs, virtual-desktop origin (signed).</param>
    /// <param name="diuTop">Window top edge, DIUs, virtual-desktop origin (signed).</param>
    /// <param name="diuWidth">Window width, DIUs.</param>
    /// <param name="diuHeight">Window height, DIUs.</param>
    /// <param name="dpiScaleX">Horizontal device scale (<c>TransformToDevice.M11</c>); 1.0 at 100%, 1.5 at 150%.</param>
    /// <param name="dpiScaleY">Vertical device scale (<c>TransformToDevice.M22</c>).</param>
    /// <returns>
    /// The physical-pixel <see cref="CaptureRect"/>, or an <see cref="CaptureRect.IsEmpty"/>
    /// rect when the inputs are degenerate (non-positive size, non-finite, or a
    /// non-positive scale).
    /// </returns>
    public static CaptureRect DiuToPhysical(
        double diuLeft, double diuTop, double diuWidth, double diuHeight,
        double dpiScaleX, double dpiScaleY)
    {
        if (!IsFinite(diuLeft) || !IsFinite(diuTop) || !IsFinite(diuWidth) || !IsFinite(diuHeight)
            || !IsFinite(dpiScaleX) || !IsFinite(dpiScaleY)
            || dpiScaleX <= 0 || dpiScaleY <= 0
            || diuWidth <= 0 || diuHeight <= 0)
        {
            return default; // IsEmpty
        }

        // Round the device-space EDGES, then derive width/height as the edge
        // difference. Rounding edges (not origin + size independently) keeps the
        // right/bottom physical edge aligned with what BitBlt reads from the
        // same scaled desktop, and prevents a half-pixel from shaving a row.
        double leftDev = diuLeft * dpiScaleX;
        double topDev = diuTop * dpiScaleY;
        double rightDev = (diuLeft + diuWidth) * dpiScaleX;
        double bottomDev = (diuTop + diuHeight) * dpiScaleY;

        int x = (int)Math.Round(leftDev, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(topDev, MidpointRounding.AwayFromZero);
        int right = (int)Math.Round(rightDev, MidpointRounding.AwayFromZero);
        int bottom = (int)Math.Round(bottomDev, MidpointRounding.AwayFromZero);

        int w = right - x;
        int h = bottom - y;
        if (w <= 0 || h <= 0) return default; // IsEmpty

        return new CaptureRect(x, y, w, h);
    }

    /// <summary>
    /// Inverse of <see cref="DiuToPhysical"/>: convert a physical-pixel
    /// <see cref="CaptureRect"/> back to a device-independent (DIU/WPF) rect suitable
    /// for positioning a window via <c>Window.Left/Top/Width/Height</c> (#957 — the
    /// overlay reads its frame from the physical-px capture store).
    ///
    /// <para>Unlike <see cref="DiuToPhysical"/> this does <b>not</b> round: DIU is a
    /// fractional unit, so dividing the physical edges by the scale yields the exact
    /// logical rect WPF expects. Round-tripping <c>physical → DIU → physical</c> at the
    /// same scale reproduces the original integer rect (the edge-rounding in
    /// <see cref="DiuToPhysical"/> is a no-op on already-integer device edges).</para>
    /// </summary>
    /// <returns>The DIU rect, or all-zero when the inputs are degenerate
    /// (empty rect, non-finite or non-positive scale) — callers treat a zero-size
    /// result as "nothing to apply".</returns>
    public static (double Left, double Top, double Width, double Height) PhysicalToDiu(
        CaptureRect rect, double dpiScaleX, double dpiScaleY)
    {
        if (rect.IsEmpty
            || !IsFinite(dpiScaleX) || !IsFinite(dpiScaleY)
            || dpiScaleX <= 0 || dpiScaleY <= 0)
        {
            return (0, 0, 0, 0);
        }

        double left = rect.X / dpiScaleX;
        double top = rect.Y / dpiScaleY;
        double width = rect.Width / dpiScaleX;
        double height = rect.Height / dpiScaleY;
        return (left, top, width, height);
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}
