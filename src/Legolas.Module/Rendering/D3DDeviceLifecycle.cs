using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.Direct3D9;
using Vortice.DXGI;
using D2D = Vortice.Direct2D1.D2D1;
using D3D9 = Vortice.Direct3D9.D3D9;
using D3D11 = Vortice.Direct3D11.D3D11;
using D3D9Format = Vortice.Direct3D9.Format;
using D3D9Usage = Vortice.Direct3D9.Usage;
using D9Pool = Vortice.Direct3D9.Pool;
using D11Usage = Vortice.Direct3D11.ResourceUsage;
using D9PresentParameters = Vortice.Direct3D9.PresentParameters;
using D9SwapEffect = Vortice.Direct3D9.SwapEffect;
using D9PresentInterval = Vortice.Direct3D9.PresentInterval;
using D9DeviceType = Vortice.Direct3D9.DeviceType;
using D9CreateFlags = Vortice.Direct3D9.CreateFlags;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using D2DFeatureLevel = Vortice.Direct2D1.FeatureLevel;
using DxgiFormat = Vortice.DXGI.Format;

namespace Legolas.Rendering;

/// <summary>
/// Owns the D3D11 + D3D9Ex pair and the shared-handle texture that bridges
/// them, so a Direct2D render target on the D3D11 side can be presented by
/// WPF's D3DImage on the D3D9 side. Single-threaded, UI-thread only.
///
/// Why two devices: D3DImage was built for D3D9 and only accepts an
/// IDirect3DSurface9 in SetBackBuffer. Modern Direct2D (1.1+) wants a D3D11
/// device. Bridging is a documented dance: create a D3D11 texture with the
/// Shared MISC flag, take its DXGI shared handle, ask a D3D9Ex device to
/// open the same memory as a D3D9 surface, and hand that surface to D3DImage.
/// Both devices then point at the same pixels — D2D writes them, D3DImage
/// reads them, no CPU-side copy.
///
/// Lifetime: device pair + factories created once and held for the life of
/// the surface. The shared texture + render target + D3D9 surface get torn
/// down and rebuilt on every size change (documented constraint of
/// SetBackBuffer — different size means a fresh surface). Resources are not
/// disposable individually; call <see cref="Dispose"/> for everything.
///
/// Step-G work: device-lost recovery, RDP software fallback (the
/// <c>enableSoftwareFallback</c> path on D3DImage.SetBackBuffer), per-monitor
/// DPI. Step-B (this file) covers the happy path so a test rectangle can
/// validate the pipeline end to end.
/// </summary>
internal sealed class D3DDeviceLifecycle : IDisposable
{
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID3D11Device _d3d11Device;
    private readonly ID3D11DeviceContext _d3d11Context;
    private readonly IDirect3D9Ex _d3d9Ex;
    private readonly IDirect3DDevice9Ex _d3d9Device;

    private ID3D11Texture2D? _sharedTexture;
    private IDirect3DTexture9? _d3d9Texture;
    private IDirect3DSurface9? _d3d9Surface;
    private ID2D1RenderTarget? _renderTarget;
    private int _width;
    private int _height;

    public D3DDeviceLifecycle()
    {
        // D2D 1.1 single-threaded factory — we only render on the dispatcher,
        // multithreaded mode just adds lock contention.
        _d2dFactory = D2D.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);

        // D3D11 device with BgraSupport (required for D2D interop). Hardware
        // driver type — no software fallback yet; that's step G's job.
        // Use locals + assign so the compiler can see the fields are
        // initialised after CheckError()'s throw-on-failure.
        D3D11.D3D11CreateDevice(
            adapter: null,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            // Feature levels: prefer 11.x but accept down to 10.0 so older
            // hardware still gets D2D acceleration.
            new[] {
                D3DFeatureLevel.Level_11_1, D3DFeatureLevel.Level_11_0,
                D3DFeatureLevel.Level_10_1, D3DFeatureLevel.Level_10_0,
            },
            out var device,
            out _,
            out var context).CheckError();
        _d3d11Device = device;
        _d3d11Context = context;

        // D3D9Ex device. Windowed presentation parameters with a 1×1 dummy
        // back buffer because we never actually render to the swapchain — we
        // only need the device for the shared-surface open call.
        _d3d9Ex = D3D9.Direct3DCreate9Ex();
        var present = new D9PresentParameters
        {
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = D3D9Format.Unknown,
            BackBufferCount = 1,
            SwapEffect = D9SwapEffect.Discard,
            DeviceWindowHandle = GetDesktopWindow(),
            Windowed = true,
            PresentationInterval = D9PresentInterval.Default,
        };
        _d3d9Device = _d3d9Ex.CreateDeviceEx(
            adapter: 0,
            D9DeviceType.Hardware,
            focusWindow: IntPtr.Zero,
            // Multithreaded + FpuPreserve are the canonical flags for shared
            // surfaces; HardwareVertexProcessing because we never draw on the
            // D3D9 side and software vertex processing would just be slower
            // bookkeeping.
            D9CreateFlags.HardwareVertexProcessing | D9CreateFlags.Multithreaded | D9CreateFlags.FpuPreserve,
            present);
    }

    /// <summary>
    /// Pixel pointer to hand to <see cref="System.Windows.Interop.D3DImage.SetBackBuffer"/>.
    /// <see cref="IntPtr.Zero"/> when no surface is currently allocated (e.g.
    /// before the first <see cref="EnsureSurface"/> call or after Dispose).
    /// </summary>
    public IntPtr D3D9SurfacePointer => _d3d9Surface?.NativePointer ?? IntPtr.Zero;

    /// <summary>
    /// D2D render target backing the shared texture. Caller wraps draw calls
    /// in BeginDraw / EndDraw and gets WPF-visible pixels via D3DImage.
    /// Null until the first <see cref="EnsureSurface"/> succeeds.
    /// </summary>
    public ID2D1RenderTarget? RenderTarget => _renderTarget;

    public ID2D1Factory1 Factory => _d2dFactory;

    /// <summary>Force any pending GPU work on the D3D11 side to flush, so the
    /// D3D9 read sees the latest D2D draws. Cheap; safe to call every frame.</summary>
    public void FlushD3D11() => _d3d11Context.Flush();

    /// <summary>
    /// Allocate (or reallocate, on size change) the shared D3D11 texture, the
    /// D3D9 surface that aliases it, and a D2D render target on top of it.
    /// Idempotent for unchanged sizes. Pass strictly positive dimensions —
    /// callers should skip the call when the host element has zero extent.
    /// </summary>
    public void EnsureSurface(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == _width && height == _height && _renderTarget is not null) return;

        DisposeSurfaceResources();

        // 1. D3D11 texture with the Shared MISC flag — that's what gets us a
        //    DXGI shared handle the D3D9 device can reopen. BGRA format
        //    matches D2D's preferred surface format and D3DImage's expected
        //    A8R8G8B8 layout exactly (the byte order is the same; the names
        //    differ across the D3D9/DXGI eras).
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = D11Usage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared,
        };
        _sharedTexture = _d3d11Device.CreateTexture2D(desc);

        // 2. Pull the legacy SharedHandle (NT-handle path is the modern one
        //    but D3D9Ex.CreateTexture only takes the legacy HANDLE — and the
        //    Shared MISC flag we set above is the matching legacy flag).
        var dxgiResource = _sharedTexture.QueryInterface<IDXGIResource>();
        var sharedHandle = dxgiResource.SharedHandle;
        dxgiResource.Dispose();

        // 3. Open the same memory on D3D9Ex. The CreateTexture overload that
        //    takes a `ref IntPtr` for the handle is the open-shared path.
        //    A8R8G8B8 byte-for-byte matches BGRA8 from the D3D11 side.
        _d3d9Texture = _d3d9Device.CreateTexture(
            (uint)width, (uint)height,
            levels: 1,
            D3D9Usage.RenderTarget,
            D3D9Format.A8R8G8B8,
            D9Pool.Default,
            ref sharedHandle);
        _d3d9Surface = _d3d9Texture.GetSurfaceLevel(0);

        // 4. D2D render target on the D3D11 texture's DXGI surface view.
        //    Premultiplied alpha so transparent pixels pass through to
        //    whatever's behind the WPF window cleanly.
        var dxgiSurface = _sharedTexture.QueryInterface<IDXGISurface>();
        var rtProps = new RenderTargetProperties(
            RenderTargetType.Default,
            new Vortice.DCommon.PixelFormat(DxgiFormat.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dpiX: 96.0f, dpiY: 96.0f,
            RenderTargetUsage.None,
            D2DFeatureLevel.Default);
        _renderTarget = _d2dFactory.CreateDxgiSurfaceRenderTarget(dxgiSurface, rtProps);
        dxgiSurface.Dispose();

        _width = width;
        _height = height;
    }

    private void DisposeSurfaceResources()
    {
        _renderTarget?.Dispose();
        _renderTarget = null;
        _d3d9Surface?.Dispose();
        _d3d9Surface = null;
        _d3d9Texture?.Dispose();
        _d3d9Texture = null;
        _sharedTexture?.Dispose();
        _sharedTexture = null;
        _width = 0;
        _height = 0;
    }

    public void Dispose()
    {
        DisposeSurfaceResources();
        _d3d9Device?.Dispose();
        _d3d9Ex?.Dispose();
        _d3d11Context?.Dispose();
        _d3d11Device?.Dispose();
        _d2dFactory?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
