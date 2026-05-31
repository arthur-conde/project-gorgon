using System;
using System.Windows;
using Microsoft.Extensions.Logging;
using Mithril.Overlay;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Shell-side <see cref="IMapBboxDrawController"/>. <see cref="BeginDraw"/> opens
/// a transient Snipping-Tool-style <see cref="RegionSnipWindow"/> spanning the
/// virtual desktop; on confirm it (1) PERSISTS the snipped absolute-virtual-desktop
/// PHYSICAL rect to the shell-owned <see cref="IMapCaptureRectStore"/> — the
/// authoritative persistence path, independent of window state (#947); the physical
/// rect is resolved at snip-confirm time from the snip window's own device scale so
/// the read path needs no DPI work — and (2) moves/resizes the <b>overlay window</b>
/// to cover the snipped region (using the DIU rect, since the overlay is a DIU/WPF
/// surface) for immediate visual feedback / snip-time consistency.
///
/// <para><b>#947:</b> persistence no longer rides the overlay window's
/// <c>SizeChanged</c>/<c>LocationChanged</c> binder. Before, a snip only stuck if
/// the overlay window was realized; now the store is written directly so the
/// region survives even when the overlay was never shown. Applying the rect to the
/// overlay (when realized) is kept purely for visual feedback — setting only
/// Left/Top/Width/Height is within the allowed surface (the existing
/// <c>WindowLayoutBinder.Apply</c> does the same); this controller does NOT mutate
/// the overlay's Topmost/WindowStyle/AllowsTransparency/Close. The Legolas binder
/// (→ <c>LegolasSettings.MapOverlay</c>) is untouched; full overlay-reads-shell-store
/// consolidation is a follow-up.</para>
///
/// <para>All WPF work runs on the overlay window's dispatcher (BeginDraw is
/// invoked from <c>DrawMapBboxCommand</c>, which may run off the UI thread). The
/// store write is window-independent and runs inline.</para>
///
/// <para><b>Manual-verify (needs running PG):</b> the live drag, the dim/hole
/// visuals, and that the snipped rect → persisted store → BitBlt capture rect all
/// coincide on a scaled (≠100% DPI) and a multi-monitor layout. The pure rect
/// math is unit-tested (<see cref="SnipRectMath"/>,
/// <see cref="ApplyVirtualDesktopRectToOverlay"/>).</para>
/// </summary>
public sealed class MapBboxDrawController : IMapBboxDrawController
{
    private readonly IOverlayWindow _overlay;
    private readonly IMapCaptureRectStore? _store;
    private readonly ILogger? _logger;

    /// <summary>
    /// A confirmed snip carries BOTH frames: the absolute-virtual-desktop DIU rect
    /// (mirrored onto the overlay window for visual feedback — the overlay is a
    /// DIU/WPF surface) and the absolute-virtual-desktop PHYSICAL rect (persisted to
    /// the store — what BitBlt reads). The physical rect is computed once at
    /// snip-confirm time from the snip window's own live device scale (#947), so the
    /// read path stays frame-independent. <c>Physical</c> may be null even on a
    /// confirmed DIU selection if the window's transform was unavailable.
    /// </summary>
    internal readonly record struct SnipResult(Rect Diu, CaptureRect? Physical);

    /// <summary>Test seam: build the transient selector. Overridden in tests so the
    /// live drag isn't required; production uses the real WPF window.</summary>
    private readonly Func<SnipResult?> _snip;

    public MapBboxDrawController(
        IOverlayWindow overlay,
        IMapCaptureRectStore? store = null,
        ILogger? logger = null)
        : this(overlay, store, logger, snip: null)
    {
    }

    internal MapBboxDrawController(
        IOverlayWindow overlay,
        IMapCaptureRectStore? store,
        ILogger? logger,
        Func<SnipResult?>? snip)
    {
        _overlay = overlay;
        _store = store;
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

        SnipResult? selected;
        try
        {
            selected = _snip();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Map-bbox snip failed; capture region unchanged.");
            return;
        }

        if (selected is not { } result)
        {
            _logger?.LogInformation("Map-bbox snip cancelled; capture region unchanged.");
            return;
        }

        var rect = result.Diu;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            _logger?.LogInformation("Map-bbox snip produced a degenerate rect; capture region unchanged.");
            return;
        }

        // Authoritative persistence (#947): write the snipped absolute-virtual-desktop
        // PHYSICAL rect to the shell-owned store directly, independent of window state.
        // The physical rect was resolved at snip-confirm time from the snip window's
        // own device scale, so the store is frame-independent (the provider reads it
        // verbatim). Fail-soft if the store isn't wired (unit-test graphs), the
        // physical rect couldn't be resolved, or the write throws — the snip still
        // applies to the overlay for the current session.
        if (result.Physical is { } physical && !physical.IsEmpty)
        {
            try
            {
                _store?.Set(physical);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Persisting the snipped capture rect failed; the region applies to this session only.");
            }
        }
        else
        {
            _logger?.LogWarning("Snip produced no physical rect (window transform unavailable); capture region not persisted.");
        }

        // Visual feedback: mirror the snip onto the overlay window when realized.
        // No longer the persistence path — purely snip-time consistency. The overlay
        // is a DIU/WPF surface, so it takes the DIU rect.
        ApplyVirtualDesktopRectToOverlay(_overlay.Window, rect);
        _logger?.LogInformation(
            "Map capture region set to {Width}x{Height} at ({Left},{Top}) (DIU).",
            (int)Math.Round(rect.Width), (int)Math.Round(rect.Height),
            (int)Math.Round(rect.X), (int)Math.Round(rect.Y));
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

    private static SnipResult? ShowRealSnipWindow()
    {
        var snip = new RegionSnipWindow();
        snip.ShowDialog();
        return snip.SelectedRect is { } diu
            ? new SnipResult(diu, snip.SelectedPhysicalRect)
            : null;
    }
}
