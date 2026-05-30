namespace Mithril.Overlay;

/// <summary>
/// Source of the live in-game map zoom factor (PG's "Zoom level: X.XX"
/// readout) for projection. Read per-tick by the overlay's projection driver
/// and by <see cref="IOverlaySceneContext.Project"/>, so a slider drag
/// Legolas-side updates pin positions in the next frame without any explicit
/// "tell the overlay to re-project" call.
///
/// <para><b>Default registration (Mithril.Overlay).</b>
/// <see cref="DependencyInjection.OverlayServiceCollectionExtensions.AddMithrilOverlay"/>
/// registers <see cref="FixedOverlayZoomSource"/> with a 1.0 multiplier so
/// the platform stays usable for consumers that don't surface a zoom
/// concept (Gwaihir POI markers; future static-texture surfaces). Legolas
/// overrides the registration with an adapter that reads
/// <c>SessionState.CurrentMapZoom</c>, so the existing zoom-slider flow
/// drives projection without any new producer interface in
/// <c>Mithril.Overlay</c>.</para>
///
/// <para><b>Reads must be cheap and lock-free.</b> Called once per
/// <see cref="IOverlaySceneContext.Project"/> invocation (potentially many
/// times per frame) and once per
/// <see cref="Internal.OverlayWindowService"/> projection tick. A field
/// snapshot or <c>volatile</c> read is fine; anything that allocates
/// belongs in the implementation's update path, not the hot read path.</para>
///
/// <para>v1 contract is read-only — there's no zoom-changed event. The
/// projection driver pulls per tick, so a stale read is at most one frame
/// behind. If a future consumer needs change notifications, layer them via
/// <see cref="System.ComponentModel.INotifyPropertyChanged"/> on the
/// concrete implementation rather than widening this interface.</para>
/// </summary>
public interface IOverlayZoomSource
{
    /// <summary>The current in-game map zoom factor. Must be finite and
    /// non-zero; the projection driver tolerates non-finite values by
    /// substituting 1.0 defensively, but producers should never let a NaN
    /// surface here.</summary>
    double CurrentZoom { get; }
}

/// <summary>
/// Constant zoom source &#8212; the platform default. Used by
/// <see cref="DependencyInjection.OverlayServiceCollectionExtensions.AddMithrilOverlay"/>
/// so the overlay window functions without a Legolas-style zoom concept.
/// Legolas overrides the DI registration with its
/// <c>SessionState.CurrentMapZoom</c> adapter.
/// </summary>
public sealed class FixedOverlayZoomSource : IOverlayZoomSource
{
    public FixedOverlayZoomSource(double zoom)
    {
        if (!double.IsFinite(zoom) || zoom <= 0)
            throw new ArgumentOutOfRangeException(nameof(zoom),
                "Fixed overlay zoom must be a positive finite value.");
        CurrentZoom = zoom;
    }

    public double CurrentZoom { get; }
}
