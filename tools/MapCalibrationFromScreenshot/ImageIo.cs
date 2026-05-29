using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Mithril.Tools.MapCalibrationFromScreenshot;

/// <summary>
/// Loads PNGs into <see cref="GrayImage"/> form for NCC consumption.
/// System.Drawing keeps the dependency surface the same as
/// <see cref="MapTextureExtractor"/> and <see cref="IconTemplateExtractor"/>;
/// pure Windows desktop tool, no cross-platform concern.
/// </summary>
internal static class ImageIo
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

    /// <summary>Downsamples by integer factor with box-averaging. Used to make NCC tractable on full-resolution textures.</summary>
    public static GrayImage Downsample(GrayImage src, int factor)
    {
        if (factor < 1) throw new ArgumentOutOfRangeException(nameof(factor));
        if (factor == 1) return src;
        int dw = src.Width / factor;
        int dh = src.Height / factor;
        var dst = new byte[dw * dh];
        int f2 = factor * factor;
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int sum = 0;
                int sx0 = x * factor;
                int sy0 = y * factor;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (sy0 + dy) * src.Width + sx0;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        sum += src.Pixels[row + dx];
                    }
                }
                dst[y * dw + x] = (byte)(sum / f2);
            }
        }
        return new GrayImage(dw, dh, dst);
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

    private static (byte[] Bgra, int Width, int Height) ReadBgra(string path)
    {
        using var bmp = new Bitmap(path);
        // Force Format32bppArgb so LockBits gives us a consistent BGRA layout in
        // memory (Windows convention).
        using var canonical = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canonical))
        {
            g.DrawImageUnscaled(bmp, 0, 0);
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
