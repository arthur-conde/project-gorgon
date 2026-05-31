using System;
using System.Windows;
using Microsoft.Extensions.Logging;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Shell-side <see cref="IMapBboxDrawController"/>. <see cref="BeginDraw"/> opens
/// a transient Snipping-Tool-style <see cref="RegionSnipWindow"/> spanning the
/// virtual desktop; on confirm it moves/resizes the <b>overlay window</b> to
/// cover the snipped region (#940 one-rect model, spec §7 — the overlay frame IS
/// the capture region and the calibration frame). Setting the overlay's
/// Left/Top/Width/Height is within the allowed surface (the existing
/// <c>WindowLayoutBinder.Apply</c> does the same), and the binder's
/// LocationChanged/SizeChanged handlers persist the new bounds automatically;
/// this controller adds NO persistence and does NOT mutate the overlay's
/// Topmost/WindowStyle/AllowsTransparency/Close.
///
/// <para>All WPF work runs on the overlay window's dispatcher (BeginDraw is
/// invoked from <c>DrawMapBboxCommand</c>, which may run off the UI thread).</para>
///
/// <para><b>Manual-verify (needs running PG):</b> the live drag, the dim/hole
/// visuals, and that the snipped rect → overlay bounds → BitBlt capture rect all
/// coincide on a scaled (≠100% DPI) and a multi-monitor layout. The pure rect
/// math is unit-tested (<see cref="SnipRectMath"/>,
/// <see cref="ApplyVirtualDesktopRectToOverlay"/>).</para>
/// </summary>
public sealed class MapBboxDrawController : IMapBboxDrawController
{
    private readonly IOverlayWindow _overlay;
    private readonly IMapCaptureRegionProvider _region;
    private readonly ILogger? _logger;

    /// <summary>Test seam: build the transient selector. Overridden in tests so the
    /// live drag isn't required; production uses the real WPF window.</summary>
    private readonly Func<Rect?> _snip;

    public MapBboxDrawController(
        IOverlayWindow overlay,
        IMapCaptureRegionProvider region,
        ILogger? logger = null)
        : this(overlay, region, logger, snip: null)
    {
    }

    internal MapBboxDrawController(
        IOverlayWindow overlay,
        IMapCaptureRegionProvider region,
        ILogger? logger,
        Func<Rect?>? snip)
    {
        _overlay = overlay;
        _region = region;
        _logger = logger;
        _snip = snip ?? ShowRealSnipWindow;
    }

    public void BeginDraw()
    {
        var window = _overlay.Window;
        var dispatcher = window.Dispatcher;

        if (dispatcher.CheckAccess())
            RunSnip();
        else
            dispatcher.Invoke(RunSnip);
    }

    private void RunSnip()
    {
        _logger?.LogInformation("Map-bbox snip armed; drag a rectangle over the in-game map.");

        Rect? selected;
        try
        {
            selected = _snip();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Map-bbox snip failed; capture region unchanged.");
            return;
        }

        if (selected is not { } rect)
        {
            _logger?.LogInformation("Map-bbox snip cancelled; capture region unchanged.");
            return;
        }

        ApplyVirtualDesktopRectToOverlay(_overlay.Window, rect);
        _logger?.LogInformation(
            "Map capture region set to {Width}x{Height} at ({Left},{Top}) (DIU).",
            (int)Math.Round(rect.Width), (int)Math.Round(rect.Height),
            (int)Math.Round(rect.X), (int)Math.Round(rect.Y));
        _ = _region; // the live overlay bounds ARE the region now (one-rect model).
    }

    /// <summary>
    /// Apply a virtual-desktop-DIU rect onto the overlay window's bounds. Pure
    /// w.r.t. the window: sets only Left/Top/Width/Height (allowed), never the
    /// forbidden invariants. Extracted as a testable seam (callable with a bare
    /// <see cref="Window"/> on an STA test). The binder persists the change.
    /// </summary>
    internal static void ApplyVirtualDesktopRectToOverlay(Window window, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        window.Left = rect.X;
        window.Top = rect.Y;
        window.Width = rect.Width;
        window.Height = rect.Height;
    }

    private static Rect? ShowRealSnipWindow()
    {
        var snip = new RegionSnipWindow();
        snip.ShowDialog();
        return snip.SelectedRect;
    }
}
