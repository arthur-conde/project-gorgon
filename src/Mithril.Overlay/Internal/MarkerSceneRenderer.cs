using Microsoft.Extensions.Logging;
using Mithril.MapCalibration;
using Vortice.Direct2D1;

namespace Mithril.Overlay.Internal;

/// <summary>
/// Pure-ish Direct2D draw dispatcher for projected markers. Each consumer
/// registers a drawer keyed on its concrete <see cref="IMarkerStyle"/> type
/// via <see cref="RegisterDrawer{TStyle}"/>; per tick the renderer iterates
/// the projected list and dispatches to the matching drawer.
///
/// <para>v1 dispatch is a <c>Dictionary&lt;Type, MarkerDrawer&gt;</c>
/// keyed on <c>style.GetType()</c>. The issue body calls for a switch in v1
/// but with no concrete styles shipped in this scaffold PR, a type-keyed
/// drawer registry is the smallest forward-compat shape that lets tests
/// verify dispatch &#8212; the type-switch suggestion was a hint at the
/// dispatch granularity, not a literal switch statement. When the migration
/// PRs land Legolas's drawers (<c>LegolasSurveyMarkerDrawer</c> etc.) they
/// call <see cref="RegisterDrawer{TStyle}"/> at module activation.</para>
///
/// <para><b>Unregistered styles.</b> A marker whose <see cref="IMarkerStyle"/>
/// has no registered drawer is silently skipped this PR &#8212; per the
/// brief, the v2 cut (when there's a real registry surface) will log/throw.
/// A first-encounter trace log is emitted per unknown type so the case is
/// at least visible during the migration window.</para>
/// </summary>
internal sealed class MarkerSceneRenderer
{
    /// <summary>Pure drawer: paint one marker at a pixel into the target.</summary>
    public delegate void MarkerDrawer(IMarkerStyle style, PixelPoint pixel, ID2D1RenderTarget rt, ID2D1Factory factory, D2DBrushCache brushes);

    private readonly Dictionary<Type, MarkerDrawer> _drawers = new();
    private readonly HashSet<Type> _missingDrawerLogged = new();
    private readonly ILogger? _logger;

    public MarkerSceneRenderer(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Register a drawer for a concrete <see cref="IMarkerStyle"/>
    /// type. Subsequent registrations for the same type replace the
    /// previous drawer.</summary>
    public void RegisterDrawer<TStyle>(Action<TStyle, PixelPoint, ID2D1RenderTarget, ID2D1Factory, D2DBrushCache> drawer)
        where TStyle : IMarkerStyle
    {
        ArgumentNullException.ThrowIfNull(drawer);
        _drawers[typeof(TStyle)] = (style, pixel, rt, factory, brushes)
            => drawer((TStyle)style, pixel, rt, factory, brushes);
    }

    /// <summary>True when at least one drawer is registered. Lets the
    /// projection driver short-circuit when the scaffold is wired but no
    /// consumer has plugged drawers in yet (e.g. immediately after
    /// <see cref="OverlayWindowService"/> starts, before Legolas activates).</summary>
    public bool HasAnyDrawer => _drawers.Count > 0;

    /// <summary>Number of registered drawer types &#8212; internal helper
    /// for diagnostics.</summary>
    internal int DrawerCount => _drawers.Count;

    /// <summary>Render one frame's projected markers. Each marker's style
    /// type is looked up in the drawer registry; misses are silently
    /// skipped (with a trace log on first encounter per type).</summary>
    public void Render(
        IReadOnlyList<(PixelPoint Pixel, IMarkerStyle Style)> markers,
        ID2D1RenderTarget rt,
        ID2D1Factory factory,
        D2DBrushCache brushes)
    {
        if (markers.Count == 0) return;

        for (var i = 0; i < markers.Count; i++)
        {
            var (pixel, style) = markers[i];
            var type = style.GetType();
            if (!_drawers.TryGetValue(type, out var drawer))
            {
                if (_missingDrawerLogged.Add(type))
                {
                    _logger?.LogTrace(
                        "MarkerSceneRenderer: no drawer registered for style type {StyleType}; markers of this type will be silently skipped",
                        type.FullName);
                }
                continue;
            }
            drawer(style, pixel, rt, factory, brushes);
        }
    }
}
