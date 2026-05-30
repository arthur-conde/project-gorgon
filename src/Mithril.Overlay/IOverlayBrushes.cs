using System.Windows.Media;
using Vortice.Direct2D1;

namespace Mithril.Overlay;

/// <summary>
/// Read-only brush surface exposed to scene drawers via
/// <see cref="IOverlaySceneContext.Brushes"/>. The host-owned cache
/// (<see cref="D2DBrushCache"/>) implements this and keeps its lifecycle
/// surface (Bind / Reset / Dispose) <c>internal</c> so a misbehaving
/// drawer can't tear down the cache mid-frame and starve sibling drawers
/// of brushes.
///
/// <para><b>Threading.</b> Like every other <see cref="IOverlaySceneContext"/>
/// member, the cache is dispatcher-affined: call <see cref="Get"/> only
/// from the scene-drawer callback thread. Stashing the returned brush
/// (or the <c>IOverlayBrushes</c> reference) and using it from another
/// thread &#8212; or after the callback returns &#8212; is undefined behaviour
/// because the host may rebind the underlying render target on the next
/// frame and dispose every brush in the cache.</para>
/// </summary>
public interface IOverlayBrushes
{
    /// <summary>Get-or-create a brush for the given color. Returns
    /// <see langword="null"/> when no render target is bound &#8212;
    /// callers should treat that as "nothing to draw" and short-circuit
    /// the layer.</summary>
    ID2D1SolidColorBrush? Get(Color color);
}
