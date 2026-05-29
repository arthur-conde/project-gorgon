using Legolas.Flow;
using Legolas.ViewModels;
using Mithril.Overlay;

namespace Legolas.Rendering;

/// <summary>
/// Legolas's override of <see cref="IOverlayZoomSource"/> &#8212; reads the
/// live in-game map zoom from <see cref="SessionState.CurrentMapZoom"/>
/// (the same value the title-bar zoom slider drives). Registered in
/// <c>LegolasModule.Register</c> ahead of <c>AddMithrilOverlay</c> so
/// <c>TryAddSingleton</c> picks Legolas's adapter over the platform default
/// <see cref="FixedOverlayZoomSource"/>.
///
/// <para>The read is a single property access on the session state (a plain
/// double-backed observable); no allocation, no synchronisation. Stale
/// reads are at most one frame behind &#8212; matches the per-tick
/// projection driver's polling cadence.</para>
///
/// <para>Defensive bounds &#8212; if the session's zoom were ever
/// non-finite or zero (shouldn't happen; the slider's <c>Minimum</c> is
/// <c>0.13</c>), <see cref="IOverlayZoomSource"/>'s contract lets the
/// projection driver substitute 1.0. We surface the raw value here and
/// let the driver's defensive clamp do the substitution; producers should
/// never let NaN reach this point, but the layered defence keeps a single
/// bad write from poisoning a frame.</para>
/// </summary>
internal sealed class LegolasOverlayZoomSource : IOverlayZoomSource
{
    private readonly SessionState _session;

    public LegolasOverlayZoomSource(SessionState session)
    {
        _session = session;
    }

    public double CurrentZoom => _session.CurrentMapZoom;
}
