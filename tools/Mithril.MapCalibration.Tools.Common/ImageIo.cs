using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Mithril.MapCalibration.Detection;

namespace Mithril.Tools.MapCalibration.Common;

/// <summary>
/// Loads PNGs into <see cref="GrayImage"/> form for NCC consumption.
/// System.Drawing keeps the dependency surface the same as
/// <see cref="MapTextureExtractor"/> and <see cref="IconTemplateExtractor"/>;
/// pure Windows desktop tool, no cross-platform concern.
/// </summary>
public static class ImageIo
{
    /// <summary>Loads a PNG as a grayscale image (ITU-R BT.601 luma).</summary>
    public static GrayImage LoadGray(string path)
    {
        var (bgra, w, h) = ReadBgra(path);
        var gray = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            byte b = bgra[i * 4 + 0];
            byte g = bgra[i * 4 + 1];
            byte r = bgra[i * 4 + 2];
            // BT.601 — same coefficients OpenCV's cvtColor default uses.
            gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }
        return new GrayImage(w, h, gray);
    }

    /// <summary>Loads only the alpha channel of a PNG (suitable for an NCC mask).</summary>
    public static GrayImage LoadAlphaMask(string path)
    {
        var (bgra, w, h) = ReadBgra(path);
        var alpha = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            alpha[i] = bgra[i * 4 + 3];
        }
        return new GrayImage(w, h, alpha);
    }

    /// <summary>
    /// Loads a PNG as both the grayscale luma + the alpha mask in one pass.
    /// Used for icon templates: NCC consumes both, and reading the file once
    /// halves the I/O.
    /// </summary>
    public static (GrayImage Gray, GrayImage Alpha) LoadGrayAndAlpha(string path)
    {
        var (bgra, w, h) = ReadBgra(path);
        var gray = new byte[w * h];
        var alpha = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            byte b = bgra[i * 4 + 0];
            byte g = bgra[i * 4 + 1];
            byte r = bgra[i * 4 + 2];
            gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
            alpha[i] = bgra[i * 4 + 3];
        }
        return (new GrayImage(w, h, gray), new GrayImage(w, h, alpha));
    }

    /// <summary>Saves a grayscale image as a PNG (for debug/diagnostics).</summary>
    public static void SaveGrayPng(GrayImage img, string path)
    {
        var bgra = new byte[img.Width * img.Height * 4];
        for (int i = 0; i < img.Width * img.Height; i++)
        {
            byte g = img.Pixels[i];
            bgra[i * 4 + 0] = g;
            bgra[i * 4 + 1] = g;
            bgra[i * 4 + 2] = g;
            bgra[i * 4 + 3] = 255;
        }
        WriteBgra(bgra, img.Width, img.Height, path);
    }

    /// <summary>Saves a BGRA buffer (4 bytes per pixel) as a PNG.</summary>
    public static void SaveBgraPng(byte[] bgra, int width, int height, string path)
    {
        WriteBgra(bgra, width, height, path);
    }

    /// <summary>Loads a PNG into a BGRA byte buffer for in-place annotation.</summary>
    public static (byte[] Bgra, int Width, int Height) LoadBgra(string path) => ReadBgra(path);

    /// <summary>
    /// Draws a 1-px rectangle outline into a BGRA buffer at (x, y, w, h). Used
    /// by the --debug-image mode to mark detected icon positions on a copy of
    /// the input screenshot. Clipped to image bounds.
    /// </summary>
    public static void DrawRect(byte[] bgra, int imgW, int imgH, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        void Pixel(int px, int py)
        {
            if (px < 0 || px >= imgW || py < 0 || py >= imgH) return;
            int idx = (py * imgW + px) * 4;
            bgra[idx + 0] = b;
            bgra[idx + 1] = g;
            bgra[idx + 2] = r;
            bgra[idx + 3] = 255;
        }
        for (int dx = 0; dx < w; dx++) { Pixel(x + dx, y); Pixel(x + dx, y + h - 1); }
        for (int dy = 0; dy < h; dy++) { Pixel(x, y + dy); Pixel(x + w - 1, y + dy); }
    }

    /// <summary>
    /// Draws a filled cross (+) at (cx, cy) of half-length <paramref name="halfLen"/>
    /// for marking anchor points. Used in conjunction with <see cref="DrawRect"/>
    /// to show the pivot-corrected anchor inside the matched icon rect.
    /// </summary>
    public static void DrawCross(byte[] bgra, int imgW, int imgH, int cx, int cy, int halfLen, byte r, byte g, byte b)
    {
        void Pixel(int px, int py)
        {
            if (px < 0 || px >= imgW || py < 0 || py >= imgH) return;
            int idx = (py * imgW + px) * 4;
            bgra[idx + 0] = b;
            bgra[idx + 1] = g;
            bgra[idx + 2] = r;
            bgra[idx + 3] = 255;
        }
        for (int dx = -halfLen; dx <= halfLen; dx++) Pixel(cx + dx, cy);
        for (int dy = -halfLen; dy <= halfLen; dy++) Pixel(cx, cy + dy);
    }

    private static (byte[] Bgra, int Width, int Height) ReadBgra(string path)
    {
        using var bmp = new Bitmap(path);
        // Force Format32bppArgb so LockBits gives us a consistent BGRA layout in
        // memory (Windows convention).
        using var canonical = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canonical))
        {
            // Draw into an explicit pixel-sized rectangle, NOT DrawImageUnscaled.
            // DrawImageUnscaled honors the source bitmap's DPI metadata and draws
            // at its *physical* size (pixels * 96/DPI). A screenshot a crop/editor
            // tool saved at e.g. 300 DPI would then be drawn at ~1/3 size into the
            // top-left corner with the rest left black — silently corrupting both
            // the NCC grayscale and the debug image (detections cluster in the
            // corner, the solved scale collapses). Drawing into a Width x Height
            // dest rect maps source pixels 1:1 regardless of DPI.
            g.DrawImage(bmp, new Rectangle(0, 0, canonical.Width, canonical.Height));
        }
        var rect = new Rectangle(0, 0, canonical.Width, canonical.Height);
        var data = canonical.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = canonical.Width * 4;
            var buf = new byte[rowBytes * canonical.Height];
            for (int row = 0; row < canonical.Height; row++)
            {
                Marshal.Copy(data.Scan0 + row * data.Stride, buf, row * rowBytes, rowBytes);
            }
            return (buf, canonical.Width, canonical.Height);
        }
        finally { canonical.UnlockBits(data); }
    }

    private static void WriteBgra(byte[] bgra, int width, int height, string path)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = width * 4;
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(bgra, row * rowBytes, data.Scan0 + row * data.Stride, rowBytes);
            }
        }
        finally { bmp.UnlockBits(data); }
        bmp.Save(path, ImageFormat.Png);
    }
}
