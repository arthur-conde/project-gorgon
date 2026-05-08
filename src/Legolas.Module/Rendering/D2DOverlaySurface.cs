using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Vortice.Direct2D1;
using Color4 = Vortice.Mathematics.Color4;

namespace Legolas.Rendering;

/// <summary>
/// WPF host element for the Direct2D-rendered pin / route / wedge layer.
///
/// Wraps a <see cref="D3DImage"/>: owns the D3D11 + D3D9Ex device pair via
/// <see cref="D3DDeviceLifecycle"/>, the shared-handle texture that bridges
/// them, the Vortice D2D render target on top of it, and the
/// Lock / SetBackBuffer / draw / AddDirtyRect / Unlock cycle each frame.
/// Knows nothing about Legolas semantics — consumers subscribe to
/// <see cref="Render"/> and emit draw calls against the supplied
/// <see cref="ID2D1RenderTarget"/>.
///
/// Step B implementation: includes a debug fill (<see cref="DebugFill"/>) so
/// the pipeline can be validated visually before the real
/// <c>PinSceneRenderer</c> is wired in step C. When DebugFill is true and
/// no Render handler is attached, the surface paints a translucent magenta
/// rectangle in the top-left corner so a tester can see the GPU path is
/// alive.
/// </summary>
public sealed class D2DOverlaySurface : FrameworkElement, IDisposable
{
    private readonly Image _hostImage;
    private readonly D3DImage _d3dImage;
    private D3DDeviceLifecycle? _lifecycle;
    private bool _renderingHooked;
    private bool _disposed;

    public D2DOverlaySurface()
    {
        _d3dImage = new D3DImage();
        _hostImage = new Image
        {
            Source = _d3dImage,
            Stretch = Stretch.None,
            // Click-through: mouse events route to the parent Viewport so the
            // existing drag-to-correct gesture still works without a per-pin
            // hit-test. Mirrors the WPF version's `IsHitTestVisible="False"`
            // on the survey ItemsControl.
            IsHitTestVisible = false,
        };
        AddVisualChild(_hostImage);
        AddLogicalChild(_hostImage);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>
    /// Fires once per <see cref="CompositionTarget.Rendering"/> tick after the
    /// D2D BeginDraw + Clear and before EndDraw, so the consumer can paint into
    /// <see cref="RenderEventArgs.RenderTarget"/> without managing the device
    /// lifecycle. Subscribers must not call BeginDraw / EndDraw themselves.
    /// </summary>
    public event EventHandler<D2DRenderEventArgs>? Render;

    /// <summary>
    /// When true and no <see cref="Render"/> handler is attached, the surface
    /// paints a translucent magenta rectangle in the top-left so the GPU path
    /// is visually verifiable. Set to false once the real renderer is wired.
    /// </summary>
    public bool DebugFill { get; set; }

    /// <summary>
    /// Force a redraw. Currently a no-op stub — every render tick already
    /// repaints; future dirty-rect work in step G can use this to schedule
    /// non-CompositionTarget driven invalidations.
    /// </summary>
    public void Invalidate()
    {
        // Step G placeholder. Continuous render via CompositionTarget.Rendering
        // covers our needs today; an explicit Invalidate is here so callers
        // don't have to grow a new dependency when on-demand redraw lands.
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _hostImage : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size availableSize)
    {
        _hostImage.Measure(availableSize);
        return _hostImage.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _hostImage.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_lifecycle is null)
        {
            try
            {
                _lifecycle = new D3DDeviceLifecycle();
            }
            catch
            {
                // GPU init can fail on RDP, headless CI, locked-down VMs etc.
                // Step G handles software fallback; step B fails silently so
                // the rest of the overlay still works (sans pins).
                _lifecycle = null;
                return;
            }
        }
        HookRendering(true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => HookRendering(false);

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Pause the render loop when the overlay is hidden — matches
        // FrameTimeLogger discipline so a hidden overlay doesn't burn CPU
        // dispatching no-op draws.
        HookRendering((bool)e.NewValue);
    }

    private void HookRendering(bool hook)
    {
        if (hook && !_renderingHooked && _lifecycle is not null)
        {
            CompositionTarget.Rendering += OnCompositionTargetRendering;
            _renderingHooked = true;
        }
        else if (!hook && _renderingHooked)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _renderingHooked = false;
        }
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        if (_disposed || _lifecycle is null) return;
        if (!IsVisible) return;

        // DPI awareness: ActualWidth/Height are in WPF DIPs; the D3D11
        // texture must be allocated in device pixels, and the D2D render
        // target's DPI must match so DIP-coordinate draw calls scale to
        // the right pixel size. On a 96-DPI monitor this is a no-op; on
        // 144-DPI / 192-DPI displays this is what makes the rendered
        // pin layer crisp instead of blurry-upscaled.
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;

        try
        {
            _lifecycle.EnsureSurface(w, h, (float)(96.0 * dpi.DpiScaleX));
        }
        catch
        {
            // Resize raced something; skip this frame.
            return;
        }

        var rt = _lifecycle.RenderTarget;
        var surfacePtr = _lifecycle.D3D9SurfacePointer;
        if (rt is null || surfacePtr == IntPtr.Zero) return;

        // D2D draw — start with a transparent clear so unrendered pixels are
        // truly transparent (no leftover frame), then either delegate to the
        // Render handler or paint the debug fill.
        rt.BeginDraw();
        rt.Clear(new Color4(0f, 0f, 0f, 0f));

        if (Render is { } handler)
        {
            handler(this, new D2DRenderEventArgs(rt, _lifecycle.Factory, w, h));
        }
        else if (DebugFill)
        {
            using var brush = rt.CreateSolidColorBrush(new Color4(1f, 0f, 1f, 0.5f));
            rt.FillRectangle(new System.Drawing.RectangleF(8, 8, 120, 64), brush);
        }

        rt.EndDraw();

        // Force the D3D11 GPU pipeline to flush so the D3D9 surface read by
        // D3DImage sees the latest D2D output, not last frame's.
        _lifecycle.FlushD3D11();

        // Hand the shared surface to D3DImage. enableSoftwareFallback=true so
        // the overlay still paints over RDP (slow path, but working — beats
        // a hard fail).
        if (!_d3dImage.IsFrontBufferAvailable) return;
        _d3dImage.Lock();
        try
        {
            _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surfacePtr, enableSoftwareFallback: true);
            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            _d3dImage.Unlock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        HookRendering(false);
        _lifecycle?.Dispose();
        _lifecycle = null;
    }
}

/// <summary>
/// Args passed to <see cref="D2DOverlaySurface.Render"/>. The render target is
/// already inside a BeginDraw / EndDraw pair; the consumer only emits draw
/// commands against it. <see cref="Factory"/> is supplied so consumers can
/// create stroke styles + path geometries without holding a reference to the
/// device lifecycle directly.
/// </summary>
public sealed class D2DRenderEventArgs : EventArgs
{
    public ID2D1RenderTarget RenderTarget { get; }
    public ID2D1Factory1 Factory { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }

    public D2DRenderEventArgs(ID2D1RenderTarget rt, ID2D1Factory1 factory, int w, int h)
    {
        RenderTarget = rt;
        Factory = factory;
        PixelWidth = w;
        PixelHeight = h;
    }
}
