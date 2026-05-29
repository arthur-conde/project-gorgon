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

    /// <summary>
    /// Bilinear resize to an arbitrary target dimension. Used by the map-rect
    /// locator to try non-integer downsample factors — integer-only resampling
    /// can't span the "map fills the screen" case where the right factor is
    /// ~2.1× and the closest integers (2 and 3) bracket it badly.
    /// </summary>
    public static GrayImage Resize(GrayImage src, int newW, int newH)
    {
        if (newW <= 0 || newH <= 0) throw new ArgumentOutOfRangeException(nameof(newW));
        if (newW == src.Width && newH == src.Height) return src;
        var dst = new byte[newW * newH];
        double xRatio = (double)src.Width / newW;
        double yRatio = (double)src.Height / newH;
        for (int y = 0; y < newH; y++)
        {
            double srcY = (y + 0.5) * yRatio - 0.5;
            int y0 = (int)Math.Floor(srcY);
            int y1 = y0 + 1;
            if (y0 < 0) y0 = 0; else if (y0 >= src.Height) y0 = src.Height - 1;
            if (y1 < 0) y1 = 0; else if (y1 >= src.Height) y1 = src.Height - 1;
            double dy = srcY - Math.Floor(srcY);
            int row0 = y0 * src.Width;
            int row1 = y1 * src.Width;
            for (int x = 0; x < newW; x++)
            {
                double srcX = (x + 0.5) * xRatio - 0.5;
                int x0 = (int)Math.Floor(srcX);
                int x1 = x0 + 1;
                if (x0 < 0) x0 = 0; else if (x0 >= src.Width) x0 = src.Width - 1;
                if (x1 < 0) x1 = 0; else if (x1 >= src.Width) x1 = src.Width - 1;
                double dx = srcX - Math.Floor(srcX);
                double p00 = src.Pixels[row0 + x0];
                double p01 = src.Pixels[row0 + x1];
                double p10 = src.Pixels[row1 + x0];
                double p11 = src.Pixels[row1 + x1];
                double top = p00 * (1 - dx) + p01 * dx;
                double bot = p10 * (1 - dx) + p11 * dx;
                dst[y * newW + x] = (byte)(top * (1 - dy) + bot * dy);
            }
        }
        return new GrayImage(newW, newH, dst);
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
