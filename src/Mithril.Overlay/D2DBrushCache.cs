using System.Windows.Media;
using Vortice.Direct2D1;
using Color4 = Vortice.Mathematics.Color4;

namespace Mithril.Overlay;

/// <summary>
/// Caches <see cref="ID2D1SolidColorBrush"/> instances keyed on packed ARGB
/// so a scene drawer doesn't allocate a new brush per draw call. Brushes are
/// tied to a specific render target; on render-target rebuild (resize,
/// device-lost) the host calls <see cref="Reset"/> and the cache discards
/// everything.
///
/// <para><b>Narrowed public surface (#835 step 6, review iteration-1 S1).</b>
/// The class is public so the concrete type can ship under
/// <c>Mithril.Overlay</c>; scene-drawer consumers reach it via
/// <see cref="IOverlaySceneContext.Brushes"/> typed as the read-only
/// <see cref="IOverlayBrushes"/> interface. The lifecycle surface
/// (<see cref="Bind"/> / <see cref="Reset"/> / <see cref="Dispose"/>) is
/// <c>internal</c> so a misbehaving drawer can't rebind, drop, or dispose
/// the host-owned cache mid-frame &#8212; if a drawer nukes the cache,
/// every subsequent drawer this tick gets <c>null</c> from <see cref="Get"/>.</para>
///
/// <para>WPF's <c>SolidColorBrush.Freeze</c> hides the equivalent
/// allocation cost behind reference-counted internals; we don't get that
/// for free in D2D land, hence the explicit cache.</para>
/// </summary>
public sealed class D2DBrushCache : IOverlayBrushes, IDisposable
{
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    private ID2D1RenderTarget? _renderTarget;

    /// <summary>
    /// Bind the cache to a render target. Subsequent <see cref="Get"/> calls
    /// allocate brushes against this target. Calling with a different target
    /// (or <c>null</c>) disposes all existing brushes. Host-owned; not
    /// exposed on <see cref="IOverlayBrushes"/>.
    /// </summary>
    internal void Bind(ID2D1RenderTarget? renderTarget)
    {
        if (ReferenceEquals(_renderTarget, renderTarget)) return;
        Reset();
        _renderTarget = renderTarget;
    }

    /// <summary>
    /// Get-or-create a brush for the given color. Returns null when no render
    /// target is bound — callers should treat that as "nothing to draw."
    /// </summary>
    public ID2D1SolidColorBrush? Get(Color color)
    {
        if (_renderTarget is null) return null;
        var key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (_brushes.TryGetValue(key, out var existing)) return existing;

        var brush = _renderTarget.CreateSolidColorBrush(new Color4(
            color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f));
        _brushes[key] = brush;
        return brush;
    }

    /// <summary>Discard all cached brushes. Called on render-target rebuild.
    /// Host-owned; not exposed on <see cref="IOverlayBrushes"/>.</summary>
    internal void Reset()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        _brushes.Clear();
    }

    /// <summary>Internal disposal entry point &#8212; the host calls this
    /// directly. <see cref="IDisposable.Dispose"/> is also implemented
    /// (explicit) so the cache is still usable with <c>using</c>-based
    /// fixtures inside the platform's own tests, but the contract for
    /// scene-drawer consumers is "do not Dispose; the host owns lifetime".</summary>
    internal void DisposeInternal() => Reset();

    void IDisposable.Dispose() => Reset();
}
