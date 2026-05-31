using System;
using Microsoft.Extensions.Logging;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IMapCaptureRegionProvider"/> backed by the SHELL-persisted capture
/// rect (#947). The capture region is a <b>persisted desktop rectangle in physical
/// pixels</b> sourced independently of any window — NOT the live overlay-window
/// geometry.
///
/// <para><b>Why this changed (#947).</b> The previous implementation derived the
/// region from the overlay window's realized geometry
/// (<c>PresentationSource.FromVisual</c> + <c>Window.Left/Top/ActualWidth/Height</c>),
/// so <see cref="Current"/> returned <see langword="null"/> whenever the overlay
/// wasn't shown — yet <c>BitBlt</c> capture (with the overlay blanked) never needs
/// the overlay shown. The user could snip a region and still get "no map bbox set".
/// Reading a persisted store removes the window-state dependency entirely.</para>
///
/// <para>The persisted rect is already in physical desktop pixels (resolved once at
/// snip-confirm time from the snip window's live <c>TransformToDevice</c> scale —
/// see <see cref="RegionSnipWindow"/> / <see cref="IMapCaptureRectStore"/>), so
/// <see cref="Current"/> returns it verbatim: zero read-time DPI work, no monitor
/// enumeration, no per-monitor affine map. Correct for single-monitor and
/// uniform-DPI multi-monitor; mixed-DPI multi-monitor is #938 manual-verify, and a
/// DPI/resolution change after snipping makes the stored rect stale (re-snip).</para>
///
/// <para><b>Fail-soft.</b> No store (unit-test graphs without the shell) / no
/// persisted rect (never snipped) / degenerate rect → <see cref="Current"/> returns
/// <see langword="null"/>; the engine surfaces "no map bbox set". Never throws into
/// the engine/host.</para>
/// </summary>
public sealed class MapCaptureRegionProvider : IMapCaptureRegionProvider
{
    private readonly IMapCaptureRectStore? _store;
    private readonly ILogger? _logger;

    public MapCaptureRegionProvider(
        IMapCaptureRectStore? store,
        ILogger? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    public CaptureRect? Current
    {
        get
        {
            // No store wired (e.g. a unit-test graph without the shell) → fail soft.
            if (_store is null) return null;

            CaptureRect? rect;
            try
            {
                rect = _store.Get();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Reading the persisted capture rect failed; treating region as unset.");
                return null;
            }

            // Never snipped (null) or a degenerate stored rect → the legitimate
            // "no bbox set" state.
            if (rect is not { } r || r.IsEmpty) return null;

            return r;
        }
    }
}
