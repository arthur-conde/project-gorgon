using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Legolas.Rendering;

/// <summary>
/// WPF host element for the Direct2D-rendered pin / route / wedge layer.
///
/// Designed as a thin shell around <see cref="D3DImage"/>: owns the D3D11
/// device + shared-handle surface ↔ D3D9Ex bridge, the Vortice
/// <c>ID2D1Factory</c> / <c>ID2D1RenderTarget</c>, and the
/// Lock / SetBackBuffer / draw / AddDirtyRect / Unlock cycle. Does not know
/// anything about Legolas semantics — the consumer subscribes to
/// <see cref="Render"/> and emits draw calls from a <c>PinScene</c> snapshot.
///
/// This file is the step-A stub: package wiring + skeleton only, no actual
/// rendering yet. The real device + render-loop implementation lands in
/// step B (test rectangle to validate the GPU path) and beyond.
/// </summary>
public sealed class D2DOverlaySurface : FrameworkElement, IDisposable
{
    private readonly Image _image;
    private readonly D3DImage _d3dImage;

    public D2DOverlaySurface()
    {
        _d3dImage = new D3DImage();
        _image = new Image
        {
            Source = _d3dImage,
            Stretch = Stretch.None,
            // Mouse events route through the parent Viewport; the surface is
            // visual-only. Mirrors the MapOverlayView "click on transparent
            // background" gesture contract.
            IsHitTestVisible = false,
        };
        AddVisualChild(_image);
        AddLogicalChild(_image);
    }

    /// <summary>
    /// Fired once per <see cref="CompositionTarget.Rendering"/> tick after the
    /// D3DImage is ready and before the back buffer is unlocked. Subscribers
    /// receive an <c>ID2D1DeviceContext</c> + frame metadata via the args
    /// (defined alongside the device-lifecycle implementation in step B).
    /// Stub keeps the event surface minimal so the XAML host can already
    /// wire to it without compile errors.
    /// </summary>
    public event EventHandler? Render;

    /// <summary>
    /// Force a redraw on the next render tick. No-op while the device is
    /// being rebuilt (e.g. after device-lost) — safe to call from any
    /// thread, harmless when called more often than the refresh rate.
    /// </summary>
    public void Invalidate()
    {
        // Step-G: dirty-rect management lives here. The stub fires Render so
        // anyone wired up can verify the event flow before the GPU path is
        // implemented.
        Render?.Invoke(this, EventArgs.Empty);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _image : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size availableSize)
    {
        _image.Measure(availableSize);
        return _image.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _image.Arrange(new Rect(finalSize));
        return finalSize;
    }

    public void Dispose()
    {
        // Step-B: dispose D2D / D3D resources here. Stub has nothing to free.
    }
}
