using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D2D = Vortice.Direct2D1.D2D1;
using D3D11Pkg = Vortice.Direct3D11.D3D11;
using D2DFeatureLevel = Vortice.Direct2D1.FeatureLevel;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using DxgiFormat = Vortice.DXGI.Format;

namespace Legolas.Tests.Rendering;

/// <summary>
/// Headless Direct2D render target for the #835-step-2 snapshot-parity tests.
/// Allocates a CPU-readable BGRA8 D3D11 texture, hangs a D2D render target off
/// its DXGI surface, lets the caller draw, then copies the pixels back into a
/// PNG via <see cref="System.Windows.Media.Imaging.PngBitmapEncoder"/>.
///
/// <para><b>Why this and not the production <c>D3DDeviceLifecycle</c>:</b>
/// the production one targets a shared GPU-resident texture for D3DImage
/// interop &#8212; not CPU-mappable. The snapshot pipeline only needs to
/// (a) render to a deterministic surface and (b) read the pixels back, so a
/// minimal hardware-D3D11 + staging-copy path is cleaner than reusing the
/// production lifecycle and grafting on a staging copy.</para>
///
/// <para><b>Driver fallback chain:</b> hardware first (fastest, matches
/// production semantics), then WARP (Windows software adapter, works on
/// headless CI without a GPU). If both fail the constructor surfaces the
/// underlying exception and the test should be gated with
/// <see cref="HeadlessD2DRenderTarget.TryCreate"/>'s null return.</para>
/// </summary>
internal sealed class HeadlessD2DRenderTarget : IDisposable
{
    private readonly ID2D1Factory _factory;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _renderTexture;
    private readonly ID3D11Texture2D _stagingTexture;
    private readonly ID2D1RenderTarget _renderTarget;
    private readonly int _width;
    private readonly int _height;

    public ID2D1Factory Factory => _factory;
    public ID2D1RenderTarget RenderTarget => _renderTarget;
    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Try to construct a headless RT at the given size. Returns null only
    /// when no usable D3D11 driver is available (hardware + WARP both fail) —
    /// the constructor wraps that case in <see cref="InvalidOperationException"/>
    /// carrying both inner messages, which is the ONLY exception caught here.
    ///
    /// <para>Everything else (<see cref="DllNotFoundException"/> for missing
    /// Vortice native libs, <see cref="BadImageFormatException"/>, argument
    /// validation, OOM, interop bugs) propagates so investigators land on
    /// the actual cause instead of a misleading "no driver" diagnostic. The
    /// caller is expected to <c>Skip</c> when this returns null and to
    /// surface the captured driver-construction exception in the Skip
    /// message via the <paramref name="driverError"/> out-param.</para>
    /// </summary>
    public static HeadlessD2DRenderTarget? TryCreate(int width, int height, out Exception? driverError)
    {
        driverError = null;
        try
        {
            return new HeadlessD2DRenderTarget(width, height);
        }
        catch (InvalidOperationException ex)
        {
            driverError = ex;
            return null;
        }
    }

    private HeadlessD2DRenderTarget(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        _width = width;
        _height = height;

        _factory = D2D.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded);

        var featureLevels = new[]
        {
            D3DFeatureLevel.Level_11_1, D3DFeatureLevel.Level_11_0,
            D3DFeatureLevel.Level_10_1, D3DFeatureLevel.Level_10_0,
        };

        // Hardware -> WARP fallback. WARP is the Windows software D3D adapter
        // ("Windows Advanced Rasterization Platform"); it works on headless CI
        // boxes without a GPU. We try hardware first because it matches
        // production rasterization semantics exactly; WARP is rasterization-
        // equivalent but the goal of these snapshot tests is byte parity to a
        // PinSceneRenderer baseline rendered on the SAME adapter, so a fallback
        // that flips both the baseline AND new render is fine.
        Exception? hardwareEx = null;
        try
        {
            D3D11Pkg.D3D11CreateDevice(
                adapter: null,
                Vortice.Direct3D.DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out var device,
                out _,
                out var context).CheckError();
            _device = device;
            _context = context;
        }
        catch (Exception ex)
        {
            hardwareEx = ex;
            try
            {
                D3D11Pkg.D3D11CreateDevice(
                    adapter: null,
                    Vortice.Direct3D.DriverType.Warp,
                    DeviceCreationFlags.BgraSupport,
                    featureLevels,
                    out var device,
                    out _,
                    out var context).CheckError();
                _device = device;
                _context = context;
            }
            catch (Exception warpEx)
            {
                throw new InvalidOperationException(
                    "No usable D3D11 driver. Hardware: " + hardwareEx.Message + "; WARP: " + warpEx.Message,
                    warpEx);
            }
        }

        // Render texture: GPU-side BGRA8 RT-bindable surface for D2D.
        _renderTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        // Staging texture: CPU-readable, copy destination. D3D11 doesn't let a
        // single texture be both RT-bindable and CPU-mappable, hence the pair.
        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        // D2D render target on the GPU texture's DXGI surface. Premultiplied
        // alpha matches PinSceneRenderer's production path (D3DDeviceLifecycle
        // uses the same AlphaMode); the snapshot comparison only holds if
        // both renderers use the same blend mode.
        var dxgiSurface = _renderTexture.QueryInterface<IDXGISurface>();
        var rtProps = new RenderTargetProperties(
            RenderTargetType.Default,
            new Vortice.DCommon.PixelFormat(DxgiFormat.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            dpiX: 96f, dpiY: 96f,
            RenderTargetUsage.None,
            D2DFeatureLevel.Default);
        _renderTarget = _factory.CreateDxgiSurfaceRenderTarget(dxgiSurface, rtProps);
        dxgiSurface.Dispose();
    }

    /// <summary>
    /// Encode the current GPU render contents as a PNG byte array. Caller is
    /// responsible for invoking BeginDraw/Clear/EndDraw on
    /// <see cref="RenderTarget"/> first; this method copies pixels to staging,
    /// maps, encodes, and unmaps.
    /// </summary>
    public byte[] EncodePng()
    {
        _context.CopyResource(_stagingTexture, _renderTexture);

        var mapped = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            // Mapped.RowPitch ≥ width*4 (driver-aligned); copy into a tight
            // BGRA8 buffer for the PNG encoder. WriteableBitmap.WritePixels
            // wants a contiguous source.
            var stride = _width * 4;
            var pixels = new byte[stride * _height];
            unsafe
            {
                var src = (byte*)mapped.DataPointer;
                for (var y = 0; y < _height; y++)
                {
                    Marshal.Copy((IntPtr)(src + y * mapped.RowPitch), pixels, y * stride, stride);
                }
            }
            var wb = new WriteableBitmap(_width, _height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, palette: null);
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, _width, _height), pixels, stride, 0);
            wb.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(wb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        finally
        {
            _context.Unmap(_stagingTexture, 0);
        }
    }

    public void Dispose()
    {
        _renderTarget?.Dispose();
        _stagingTexture?.Dispose();
        _renderTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _factory?.Dispose();
    }
}
