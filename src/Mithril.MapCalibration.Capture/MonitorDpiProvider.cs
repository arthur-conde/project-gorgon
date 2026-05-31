using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Mithril.MapCalibration.Capture;

/// <summary>Enumerates the live monitor layout (physical bounds + per-monitor DPI)
/// for the DIU→physical capture-rect map (#947).</summary>
public interface IMonitorDpiProvider
{
    /// <summary>The current monitors. Empty when enumeration fails (fail-soft).</summary>
    IReadOnlyList<MonitorDpiInfo> Monitors();
}

/// <summary>
/// CsWin32 <see cref="IMonitorDpiProvider"/> over <c>EnumDisplayMonitors</c> +
/// <c>GetMonitorInfo</c> + <c>GetDpiForMonitor</c> (#947). Physical bounds come
/// straight from <c>GetMonitorInfo().rcMonitor</c> (the same physical
/// virtual-desktop frame <c>GetDC(NULL)</c>/<c>BitBlt</c> read under PerMonitorV2);
/// the DIU bounds are reconstructed as <c>physical ÷ per-monitor scale</c>, which
/// is exact for the primary monitor and for a uniform-DPI layout, and best-effort
/// (origin-placement) for a mixed-DPI secondary — see
/// <see cref="MonitorScaleSelector"/> for the precise correctness contract.
///
/// <para>The live enumeration is manual-verify (#938); the pure selection/affine
/// math is unit-tested via <see cref="MonitorScaleSelector"/> with synthetic
/// monitor sets.</para>
/// </summary>
public sealed class MonitorDpiProvider : IMonitorDpiProvider
{
    private const double DefaultDpi = 96.0;

    private readonly ILogger? _logger;

    public MonitorDpiProvider(ILogger? logger = null) => _logger = logger;

    public IReadOnlyList<MonitorDpiInfo> Monitors()
    {
        var result = new List<MonitorDpiInfo>();
        try
        {
            // Managed-delegate (MONITORENUMPROC) overload: the closure captures
            // `result`, so no GCHandle marshalling is needed. EnumDisplayMonitors is
            // synchronous — the delegate has fully run by the time the call returns.
            BOOL ok;
            unsafe
            {
                ok = PInvoke.EnumDisplayMonitors(
                    default, (RECT*)null, (monitor, _, _, _) => AddMonitor(result, monitor), default);
            }
            if (!ok)
                _logger?.LogWarning("EnumDisplayMonitors returned false; capture-rect DPI map may be incomplete.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Monitor enumeration failed; capture-rect DPI map has no monitors (fail-soft).");
            return Array.Empty<MonitorDpiInfo>();
        }

        return result;
    }

    private BOOL AddMonitor(List<MonitorDpiInfo> list, HMONITOR monitor)
    {
        try
        {
            MONITORINFO info;
            unsafe { info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) }; }
            if (!PInvoke.GetMonitorInfo(monitor, ref info)) return true;

            // GetDpiForMonitor is a Windows 8.1+ API; the runtime guard both satisfies
            // CA1416 (no attribute propagation to call sites) and fail-softs to 96 DPI
            // (scale 1.0) on the practically-unreachable pre-8.1 host. The app ships
            // PerMonitorV2 (Win10+), so the guard is always true in practice.
            double sx = 1.0, sy = 1.0;
            if (OperatingSystem.IsWindowsVersionAtLeast(8, 1)
                && PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY).Succeeded
                && dpiX > 0 && dpiY > 0)
            {
                sx = dpiX / DefaultDpi;
                sy = dpiY / DefaultDpi;
            }

            RECT phys = info.rcMonitor;
            double physLeft = phys.left;
            double physTop = phys.top;
            double physWidth = phys.right - phys.left;
            double physHeight = phys.bottom - phys.top;

            // Reconstruct DIU bounds: physical ÷ per-monitor scale (see class docs).
            list.Add(new MonitorDpiInfo(
                DiuLeft: physLeft / sx,
                DiuTop: physTop / sy,
                DiuWidth: physWidth / sx,
                DiuHeight: physHeight / sy,
                PhysicalLeft: physLeft,
                PhysicalTop: physTop,
                ScaleX: sx,
                ScaleY: sy));
        }
        catch (Exception ex)
        {
            // Never throw across the native callback boundary; skip this monitor.
            _logger?.LogWarning(ex, "Reading a monitor's geometry/DPI failed; skipping it in the capture-rect map.");
        }

        return true;
    }
}
