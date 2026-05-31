using System;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IMapCaptureRegionProvider"/> backed by the SHELL-persisted capture
/// rect (#947). The capture region is a <b>persisted desktop rectangle</b> sourced
/// independently of any window — NOT the live overlay-window geometry.
///
/// <para><b>Why this changed (#947).</b> The previous implementation derived the
/// region from the overlay window's realized geometry
/// (<c>PresentationSource.FromVisual</c> + <c>Window.Left/Top/ActualWidth/Height</c>),
/// so <see cref="Current"/> returned <see langword="null"/> whenever the overlay
/// wasn't shown — yet <c>BitBlt</c> capture (with the overlay blanked) never needs
/// the overlay shown. The user could snip a region and still get "no map bbox set".
/// Reading a persisted store removes the window-state dependency entirely; reading
/// the store + Win32 per-monitor DPI is thread-safe, so this no longer touches the
/// UI thread / dispatcher.</para>
///
/// <para><see cref="Current"/> reads the persisted absolute virtual-desktop DIU
/// rect and converts it to the physical-pixel <see cref="CaptureRect"/> BitBlt
/// reads, using the live per-monitor DPI layout (<see cref="MonitorScaleSelector"/>
/// over <see cref="IMonitorDpiProvider"/>). The pixel math is the pure, unit-tested
/// <see cref="CaptureRectMath.DiuToPhysical"/> / <see cref="MonitorScaleSelector"/>.</para>
///
/// <para><b>Fail-soft.</b> No store (unit-test graphs without the shell) / no
/// persisted rect (never snipped) / degenerate or off-screen rect → <see cref="Current"/>
/// returns <see langword="null"/>; the engine surfaces "no map bbox set". Never
/// throws into the engine/host.</para>
/// </summary>
public sealed class MapCaptureRegionProvider : IMapCaptureRegionProvider
{
    private readonly IMapCaptureRectStore? _store;
    private readonly IMonitorDpiProvider _monitors;
    private readonly ILogger? _logger;

    public MapCaptureRegionProvider(
        IMapCaptureRectStore? store,
        IMonitorDpiProvider? monitors = null,
        ILogger? logger = null)
    {
        _store = store;
        _monitors = monitors ?? new MonitorDpiProvider(logger);
        _logger = logger;
    }

    public CaptureRect? Current
    {
        get
        {
            // No store wired (e.g. a unit-test graph without the shell) → fail soft.
            if (_store is null) return null;

            MapCaptureRectDiu? diu;
            try
            {
                diu = _store.Get();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Reading the persisted capture rect failed; treating region as unset.");
                return null;
            }

            // Never snipped → the legitimate "no bbox set" state.
            if (diu is not { } rect) return null;

            CaptureRect physical;
            try
            {
                physical = MonitorScaleSelector.ToPhysical(rect, _monitors.Monitors());
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Converting the persisted capture rect to physical pixels failed; treating region as unset.");
                return null;
            }

            return physical.IsEmpty ? null : physical;
        }
    }
}
