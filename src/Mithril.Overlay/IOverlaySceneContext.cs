using Mithril.MapCalibration;
using Vortice.Direct2D1;

namespace Mithril.Overlay;

/// <summary>
/// Per-tick draw context handed to scene drawers registered via
/// <see cref="IOverlayWindow.RegisterScene"/>. The scene drawer gets the raw
/// Direct2D render target + factory, a shared <see cref="D2DBrushCache"/>
/// bound to the current target, the current Arda area key, and a
/// <see cref="Project"/> helper that maps a world coord to a pixel via the
/// shared <see cref="IMapCalibrationService"/>.
///
/// <para><b>Layer 2 of the overlay platform</b> per the #835 reframe — this
/// is the <em>primary</em> draw API. <see cref="IWorldOverlayMarkers"/>
/// (layer 3) is a thin convenience for the "just plot these world points
/// with this style" case; everything that isn't a single world-coord point
/// (route polylines, pixel-anchored glyphs, animations, calibration
/// placement pins) belongs in a scene drawer because the consumer owns the
/// draw and pixel-space is natural.</para>
///
/// <para><b>Threading.</b> Scene drawers fire on the WPF dispatcher inside
/// the surface's <c>BeginDraw</c>/<c>EndDraw</c> pair &#8212; the same place
/// the marker renderer runs. Drawers must not retain the context, the
/// render target, the factory, or any brush past the callback; the brush
/// cache is rebound + the render target may be torn down on resize /
/// device-lost / area change.</para>
///
/// <para><b>Drawing order.</b> Scene drawers fire BEFORE the marker
/// renderer each tick (registered drawers in registration order). So a
/// scene drawer's polylines / pins land beneath the registry-managed
/// marker layer; consumers that want their pins on top should use a scene
/// drawer for the whole layer, not mix scene-drawer geometry with registry
/// markers in the same z-band.</para>
/// </summary>
public interface IOverlaySceneContext
{
    /// <summary>The bound D2D render target. Valid only for the duration of
    /// the scene-drawer callback.</summary>
    ID2D1RenderTarget RenderTarget { get; }

    /// <summary>The D2D factory the surface was created against. Valid only
    /// for the duration of the scene-drawer callback. Use for transient
    /// geometry / stroke style creation.</summary>
    ID2D1Factory Factory { get; }

    /// <summary>Brush cache bound to <see cref="RenderTarget"/>. Drawers
    /// should call <see cref="D2DBrushCache.Get"/> rather than creating
    /// their own brushes; the cache is reset on render-target rebuild so
    /// brush handles never outlive the target.</summary>
    D2DBrushCache Brushes { get; }

    /// <summary>The Arda internal area key for the current frame's player
    /// area (e.g. <c>"AreaEltibule"</c>). Empty / unknown areas are filtered
    /// out before scene drawers fire, so this is always non-null and
    /// non-empty when a drawer is invoked.</summary>
    string CurrentAreaKey { get; }

    /// <summary>Project a world coord to a pixel via the current area's
    /// calibration, accounting for the live in-game zoom (read per call
    /// from the injected <see cref="IOverlayZoomSource"/>). Returns
    /// <see langword="null"/> when the calibration service can't resolve
    /// the point &#8212; mirrors
    /// <see cref="IMapCalibrationService.WorldToWindow"/>'s null return for
    /// uncalibrated areas or out-of-range coords. Scene drawers gate
    /// world-space geometry on a non-null projection; pixel-native bits
    /// (route polyline pixels, calibration placement pins captured by
    /// pixel) skip the projection entirely.</summary>
    PixelPoint? Project(double worldX, double worldZ);
}
