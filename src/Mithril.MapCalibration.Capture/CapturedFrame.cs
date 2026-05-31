using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// A raw captured rectangle of desktop pixels in BGRA byte order (the layout
/// <c>GetDIBits</c> produces with a top-down <c>BITMAPINFOHEADER</c>:
/// 4 bytes/pixel, B then G then R then A, row-major top-to-bottom). The capture
/// layer hands this across the boundary to the BCL-only detection core via
/// <see cref="ToGray"/>; no image decoder is involved.
/// </summary>
public sealed record CapturedFrame(int Width, int Height, byte[] Bgra)
{
    /// <summary>
    /// Convert to a single-channel <see cref="GrayImage"/> using BT.601 luma
    /// (<c>0.114*B + 0.587*G + 0.299*R</c>), the same weighting the detection
    /// pipeline expects. Alpha is ignored.
    /// </summary>
    public GrayImage ToGray()
    {
        var gray = new byte[Width * Height];
        for (int i = 0; i < gray.Length; i++)
        {
            int o = i * 4;
            byte b = Bgra[o];
            byte g = Bgra[o + 1];
            byte r = Bgra[o + 2];
            gray[i] = (byte)(0.114 * b + 0.587 * g + 0.299 * r + 0.5);
        }
        return new GrayImage(Width, Height, gray);
    }

    /// <summary>Mean BT.601 luma over every pixel; the black-frame detector.</summary>
    public double MeanLuma()
    {
        if (Width <= 0 || Height <= 0) return 0.0;
        double sum = 0.0;
        int count = Width * Height;
        for (int i = 0; i < count; i++)
        {
            int o = i * 4;
            sum += 0.114 * Bgra[o] + 0.587 * Bgra[o + 1] + 0.299 * Bgra[o + 2];
        }
        return sum / count;
    }
}
