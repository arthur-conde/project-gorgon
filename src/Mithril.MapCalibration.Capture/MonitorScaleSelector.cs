using System;
using System.Collections.Generic;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// One monitor's geometry, in BOTH coordinate frames, for the DIU→physical map
/// (#947). Pure data — no Win32, so the selection logic is unit-testable
/// headlessly with the live <c>EnumDisplayMonitors</c>/<c>GetDpiForMonitor</c>
/// enumeration injected at runtime.
/// </summary>
/// <param name="DiuLeft">Monitor left edge in absolute virtual-desktop DIUs (signed).</param>
/// <param name="DiuTop">Monitor top edge in absolute virtual-desktop DIUs (signed).</param>
/// <param name="DiuWidth">Monitor width in DIUs.</param>
/// <param name="DiuHeight">Monitor height in DIUs.</param>
/// <param name="PhysicalLeft">Monitor left edge in physical pixels (the frame <c>GetDC(NULL)</c>/<c>BitBlt</c> read; signed).</param>
/// <param name="PhysicalTop">Monitor top edge in physical pixels (signed).</param>
/// <param name="ScaleX">Horizontal device scale (1.0 at 100%, 1.5 at 150%).</param>
/// <param name="ScaleY">Vertical device scale.</param>
public readonly record struct MonitorDpiInfo(
    double DiuLeft, double DiuTop, double DiuWidth, double DiuHeight,
    double PhysicalLeft, double PhysicalTop,
    double ScaleX, double ScaleY);

/// <summary>
/// Pure, WPF-free mapping from an absolute virtual-desktop DIU rect to the
/// physical-pixel <see cref="CaptureRect"/> that <c>BitBltScreenCapture</c> reads
/// (#947). The map is per-monitor:
/// <code>physical = monitorPhysicalOrigin + (diu − monitorDiuOrigin) · monitorScale</code>
///
/// <para><b>Why per-monitor and not a single global scale.</b> The app runs
/// PerMonitorV2 (<c>ApplicationHighDpiMode=PerMonitorV2</c>). Under PMv2 the
/// physical desktop frame (<c>GetDC(NULL)</c> / <c>BitBlt</c> / <c>GetClientRect</c>+
/// <c>ClientToScreen</c>) places each monitor at its native physical resolution
/// and physical origin, while WPF reports geometry in a logical DIU frame where
/// each monitor's DIU extent is its physical extent ÷ its OWN dpi scale. So the
/// scalar map <c>physical = diu · scale</c> (the original <see cref="CaptureRectMath.DiuToPhysical"/>
/// contract) is correct only when a monitor's physical origin equals its DIU origin
/// × scale — true for the primary (both origins are 0) and for a uniform-DPI layout
/// (one shared scale, so origins scale consistently), but NOT for a mixed-DPI
/// secondary monitor whose physical origin is offset by its neighbour's full
/// physical width. This class carries each monitor's physical AND DIU origin so the
/// affine map is correct across that offset.</para>
///
/// <para><b>Correctness guarantees.</b>
/// <list type="bullet">
/// <item>Single monitor: exact (origins coincide at 0).</item>
/// <item>Uniform-DPI multi-monitor (incl. negative virtual origins): exact —
/// every monitor shares one scale, so the affine map reduces to the proven
/// <see cref="CaptureRectMath.DiuToPhysical"/> identity and the per-monitor origins
/// cancel.</item>
/// <item>Mixed-DPI multi-monitor: exact <i>when</i> the injected
/// <see cref="MonitorDpiInfo"/> frames match what WPF/Windows actually lay out (the
/// physical bounds come straight from <c>GetMonitorInfo</c>; the DIU bounds are
/// reconstructed — see <see cref="MonitorDpiProvider"/>). The size always uses the
/// correct per-monitor scale; the origin placement depends on the reconstructed DIU
/// layout, which is the one piece not exercisable in CI (manual-verify #938).</item>
/// </list></para>
/// </summary>
public static class MonitorScaleSelector
{
    /// <summary>
    /// Select the monitor whose DIU bounds contain the rect's top-left origin. The
    /// origin is the anchoring corner WPF uses for <c>Window.Left/Top</c>, so the
    /// rect "belongs" to the monitor it starts on (matching how a snipped rect is
    /// drawn). Returns <see langword="null"/> when no monitor contains the origin
    /// (e.g. an empty enumeration, or a stale rect now off every screen) so the
    /// caller can fail soft.
    /// </summary>
    public static MonitorDpiInfo? SelectMonitorScale(
        MapCaptureRectDiu rect, IReadOnlyList<MonitorDpiInfo> monitors)
    {
        if (monitors is null || monitors.Count == 0) return null;

        // Half-open containment on the origin: [left, right) × [top, bottom). A point
        // on a shared edge resolves to the monitor to the right/below, which is the
        // Windows convention.
        foreach (var m in monitors)
        {
            if (rect.Left >= m.DiuLeft && rect.Left < m.DiuLeft + m.DiuWidth
                && rect.Top >= m.DiuTop && rect.Top < m.DiuTop + m.DiuHeight)
            {
                return m;
            }
        }

        return null;
    }

    /// <summary>
    /// Convert an absolute virtual-desktop DIU rect to the physical-pixel
    /// <see cref="CaptureRect"/> using the monitor it sits on. Returns an
    /// <see cref="CaptureRect.IsEmpty"/> rect when no monitor contains the origin or
    /// the inputs are degenerate (fail-soft).
    /// </summary>
    public static CaptureRect ToPhysical(MapCaptureRectDiu rect, IReadOnlyList<MonitorDpiInfo> monitors)
    {
        if (SelectMonitorScale(rect, monitors) is not { } m)
            return default; // IsEmpty — no containing monitor

        // Translate the rect's DIU origin into the monitor's local DIU frame, scale
        // by the monitor's per-axis scale, then re-anchor at the monitor's physical
        // origin. CaptureRectMath.DiuToPhysical owns the edge-rounding/degenerate
        // contract; we feed it a rect already expressed in this monitor's
        // origin-relative physical space and re-add the physical origin afterwards.
        double localLeft = rect.Left - m.DiuLeft;
        double localTop = rect.Top - m.DiuTop;

        var scaled = CaptureRectMath.DiuToPhysical(
            localLeft, localTop, rect.Width, rect.Height, m.ScaleX, m.ScaleY);
        if (scaled.IsEmpty) return default;

        int physLeft = (int)Math.Round(m.PhysicalLeft, MidpointRounding.AwayFromZero);
        int physTop = (int)Math.Round(m.PhysicalTop, MidpointRounding.AwayFromZero);
        return new CaptureRect(scaled.X + physLeft, scaled.Y + physTop, scaled.Width, scaled.Height);
    }
}
