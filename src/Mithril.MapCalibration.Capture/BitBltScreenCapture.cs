using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// <see cref="IScreenCapture"/> over GDI <c>BitBlt</c> + <c>GetDIBits</c>
/// (CsWin32). Reads the OS framebuffer for the requested desktop rect into a
/// top-down BGRA buffer. Every native handle is released in a <c>finally</c>;
/// any failure returns <see langword="null"/> and logs a <c>Warning</c> rather
/// than throwing into the orchestrator.
///
/// <para>The GDI calls are manual-verified against a running game (no CI pixel
/// test — the pure BGRA→gray + validation paths are covered by
/// <see cref="CaptureValidation"/> tests).</para>
/// </summary>
public sealed class BitBltScreenCapture : IScreenCapture
{
    private const uint SRCCOPY = 0x00CC0020;
    private const int BI_RGB = 0;

    private readonly ILogger? _logger;

    public BitBltScreenCapture(ILogger? logger = null)
    {
        _logger = logger;
    }

    public unsafe CapturedFrame? Capture(CaptureRect rect)
    {
        if (rect.IsEmpty)
        {
            _logger?.LogWarning("BitBlt capture asked for an empty rect {Width}x{Height}", rect.Width, rect.Height);
            return null;
        }

        int w = rect.Width;
        int h = rect.Height;

        HDC screenDc = default;
        HDC memDc = default;
        HBITMAP bitmap = default;
        HGDIOBJ previous = default;
        try
        {
            screenDc = PInvoke.GetDC(default);
            if (screenDc.IsNull)
            {
                _logger?.LogWarning("BitBlt capture: GetDC(screen) returned null");
                return null;
            }

            memDc = PInvoke.CreateCompatibleDC(screenDc);
            if (memDc.IsNull)
            {
                _logger?.LogWarning("BitBlt capture: CreateCompatibleDC returned null");
                return null;
            }

            bitmap = PInvoke.CreateCompatibleBitmap(screenDc, w, h);
            if (bitmap.IsNull)
            {
                _logger?.LogWarning("BitBlt capture: CreateCompatibleBitmap returned null");
                return null;
            }

            previous = PInvoke.SelectObject(memDc, bitmap);

            bool blitted = PInvoke.BitBlt(memDc, 0, 0, w, h, screenDc, rect.X, rect.Y, (ROP_CODE)SRCCOPY);
            if (!blitted)
            {
                _logger?.LogWarning("BitBlt capture: BitBlt failed for {Width}x{Height} at ({X},{Y})", w, h, rect.X, rect.Y);
                return null;
            }

            var bgra = new byte[w * h * 4];

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;   // top-down: row 0 is the top scanline
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;

            int scanned;
            fixed (byte* p = bgra)
            {
                scanned = PInvoke.GetDIBits(
                    memDc,
                    bitmap,
                    0,
                    (uint)h,
                    p,
                    &bmi,
                    DIB_USAGE.DIB_RGB_COLORS);
            }

            if (scanned == 0)
            {
                _logger?.LogWarning("BitBlt capture: GetDIBits returned 0 scanlines");
                return null;
            }

            return new CapturedFrame(w, h, bgra);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "BitBlt capture failed");
            return null;
        }
        finally
        {
            if (!memDc.IsNull && !previous.IsNull)
            {
                PInvoke.SelectObject(memDc, previous);
            }
            if (!bitmap.IsNull)
            {
                PInvoke.DeleteObject(bitmap);
            }
            if (!memDc.IsNull)
            {
                PInvoke.DeleteDC(memDc);
            }
            if (!screenDc.IsNull)
            {
                PInvoke.ReleaseDC(default, screenDc);
            }
        }
    }
}
