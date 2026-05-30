using System;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class MapRectLocatorTests
{
    private static GrayImage NoisyTexture(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var px = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double gradient = 80 + 80.0 * x / w + 60.0 * y / h;
                int v = (int)gradient + rng.Next(-30, 31);
                px[y * w + x] = (byte)Math.Clamp(v, 0, 255);
            }
        return new GrayImage(w, h, px);
    }

    [Fact]
    public void AutoDetect_recovers_origin_of_embedded_texture()
    {
        const int tw = 120, th = 90;
        var texture = NoisyTexture(tw, th, 1234);

        // Pad the texture into a larger "screenshot" with constant UI chrome,
        // at native scale (factor 1.0).
        const int padX = 20, padY = 35;
        int sw = tw + padX + 30, sh = th + padY + 25;
        var shot = new byte[sw * sh];
        Array.Fill(shot, (byte)40);
        for (int y = 0; y < th; y++)
            Buffer.BlockCopy(texture.Pixels, y * tw, shot, (y + padY) * sw + padX, tw);
        var screenshot = new GrayImage(sw, sh, shot);

        var rect = MapRectLocator.AutoDetect(screenshot, texture, minScore: 0.5);

        rect.Should().NotBeNull();
        rect!.OriginX.Should().BeCloseTo(padX, 3);
        rect.OriginY.Should().BeCloseTo(padY, 3);
    }

    [Fact]
    public void MapRect_ScreenshotToTexture_round_trips()
    {
        // Texture 200x100 rendered into a 100x50 window at origin (10, 20).
        var rect = new MapRect(OriginX: 10, OriginY: 20, Width: 100, Height: 50, TextureWidth: 200, TextureHeight: 100);

        var (tx, ty) = rect.ScreenshotToTexture(60, 45);

        // (60-10)*2 = 100 ; (45-20)*2 = 50
        tx.Should().BeApproximately(100, 1e-9);
        ty.Should().BeApproximately(50, 1e-9);
    }
}
