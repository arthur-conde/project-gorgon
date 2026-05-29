using System.Windows.Media;
using Vortice.Direct2D1;
using Color4 = Vortice.Mathematics.Color4;

namespace Mithril.Overlay.Internal;

/// <summary>
/// Caches <see cref="ID2D1SolidColorBrush"/> instances keyed on packed ARGB
/// so the renderer doesn't allocate a new brush per draw call. Brushes are
/// tied to a specific render target; on render-target rebuild (resize,
/// device-lost) call <see cref="Reset"/> and the cache discards everything.
///
/// WPF's <c>SolidColorBrush.Freeze</c> hides the equivalent allocation cost
/// behind reference-counted internals; we don't get that for free in D2D
/// land, hence the explicit cache.
/// </summary>
internal sealed class D2DBrushCache : IDisposable
{
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    private ID2D1RenderTarget? _renderTarget;

    /// <summary>
    /// Bind the cache to a render target. Subsequent <see cref="Get"/> calls
    /// allocate brushes against this target. Calling with a different target
    /// (or <c>null</c>) disposes all existing brushes.
    /// </summary>
    public void Bind(ID2D1RenderTarget? renderTarget)
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

    /// <summary>Discard all cached brushes. Called on render-target rebuild.</summary>
    public void Reset()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        _brushes.Clear();
    }

    public void Dispose() => Reset();
}
