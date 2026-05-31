using System;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Capture.Tests.Fixtures;

/// <summary>
/// Tiny synthetic-image helpers for the capture tests: a high-variance noise
/// texture (so NCC is well-defined) and a "paste a texture into a larger gray
/// canvas" blitter (the padding trick the Phase-1 <c>MapRectLocatorTests</c>
/// uses to give <c>AutoDetect</c> a recoverable origin).
/// </summary>
internal static class SyntheticMap
{
    /// <summary>A high-variance random grayscale texture.</summary>
    public static GrayImage NoisyTexture(int seed, int w, int h)
    {
        var rng = new Random(seed);
        var px = new byte[w * h];
        rng.NextBytes(px);
        return new GrayImage(w, h, px);
    }

    /// <summary>
    /// Paste <paramref name="texture"/> into a larger mid-gray canvas at
    /// (<paramref name="atX"/>, <paramref name="atY"/>), returning the canvas.
    /// </summary>
    public static GrayImage PasteInto(GrayImage texture, int canvasW, int canvasH, int atX, int atY)
    {
        var px = new byte[canvasW * canvasH];
        Array.Fill(px, (byte)128); // mid-gray background, distinct from the noise
        for (int y = 0; y < texture.Height; y++)
        {
            int dstRow = (atY + y) * canvasW + atX;
            int srcRow = y * texture.Width;
            for (int x = 0; x < texture.Width; x++)
            {
                px[dstRow + x] = texture.Pixels[srcRow + x];
            }
        }
        return new GrayImage(canvasW, canvasH, px);
    }
}
