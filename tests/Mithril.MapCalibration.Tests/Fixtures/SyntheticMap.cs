using System;
using System.Collections.Generic;
using Mithril.MapCalibration;
using Mithril.MapCalibration.Detection;

namespace Mithril.MapCalibration.Tests.Fixtures;

/// <summary>
/// Synthetic map/icon builders shared by the detector + solve-engine tests.
/// Ports the gate-study SelfTest's teardrop blitter so a cold session can run
/// the full detect→solve chain with no PG install and no real screenshot.
/// </summary>
public static class SyntheticMap
{
    public sealed record IconSpec(string Name, string LandmarkType, int Width, int Height, int Luminance);

    public static readonly IconSpec[] DefaultIcons =
    [
        new("landmark_portal", "Portal", 24, 32, 60),
        new("landmark_telepad", "TeleportationPlatform", 28, 22, 180),
        new("landmark_medipillar", "MeditationPillar", 18, 40, 110),
        new("landmark_npc", "Npc", 20, 28, 220),
    ];

    /// <summary>Noisy high-contrast terrain so NCC has signal to lock onto.</summary>
    public static byte[] MakeTexture(int width, int height, int seed)
    {
        var rng = new Random(seed);
        var data = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double gradient = 80 + 80.0 * x / width + 60.0 * y / height;
                int v = (int)gradient + rng.Next(-30, 31);
                data[y * width + x] = (byte)Math.Clamp(v, 0, 255);
            }
        return data;
    }

    /// <summary>Builds an icon template set (teardrop gray+alpha) for the given specs.</summary>
    public static IconTemplateSet BuildTemplates(IReadOnlyList<IconSpec> specs)
    {
        var templates = new List<IconTemplate>();
        foreach (var s in specs)
        {
            var (gray, alpha) = Teardrop(s.Width, s.Height, s.Luminance);
            templates.Add(new IconTemplate(s.Name, s.LandmarkType, 0.5, 0.0,
                new GrayImage(s.Width, s.Height, gray), new GrayImage(s.Width, s.Height, alpha)));
        }
        return new IconTemplateSet(templates);
    }

    /// <summary>
    /// Blit a teardrop into a gray buffer anchored at the bottom tip (pivot
    /// (0.5, 0)), so the icon's bottom-centre lands on (anchorX, anchorY).
    /// </summary>
    public static void BlitTeardrop(byte[] dest, int destW, int destH,
        double anchorX, double anchorY, int width, int height, int luminance)
    {
        int topLeftX = (int)Math.Round(anchorX - width / 2.0);
        int topLeftY = (int)Math.Round(anchorY - (height - 1));
        int outlineLum = Math.Max(0, luminance - 60);
        for (int y = 0; y < height; y++)
        {
            int dy = topLeftY + y;
            if (dy < 0 || dy >= destH) continue;
            for (int x = 0; x < width; x++)
            {
                int dx = topLeftX + x;
                if (dx < 0 || dx >= destW) continue;
                int kind = TeardropPixel(x, y, width, height);
                if (kind == 0) continue;
                dest[dy * destW + dx] = (byte)(kind == 1 ? outlineLum : luminance);
            }
        }
    }

    private static (byte[] Gray, byte[] Alpha) Teardrop(int width, int height, int luminance)
    {
        int outlineLum = Math.Max(0, luminance - 60);
        var gray = new byte[width * height];
        var alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int kind = TeardropPixel(x, y, width, height);
                int idx = y * width + x;
                if (kind == 0) { gray[idx] = 0; alpha[idx] = 0; }
                else { gray[idx] = (byte)(kind == 1 ? outlineLum : luminance); alpha[idx] = 255; }
            }
        return (gray, alpha);
    }

    private static int TeardropPixel(int x, int y, int width, int height)
    {
        double cx = (width - 1) / 2.0;
        double radius = width / 2.5;
        double circleCy = radius + 1;
        bool inShape, inInterior;
        if (y <= circleCy + 1)
        {
            double dx = x - cx;
            double dy = y - circleCy;
            double r2 = dx * dx + dy * dy;
            inShape = r2 <= radius * radius;
            inInterior = r2 <= (radius - 1.2) * (radius - 1.2);
        }
        else
        {
            double t = (y - circleCy) / (height - 1 - circleCy);
            double halfW = radius * (1.0 - t);
            inShape = Math.Abs(x - cx) <= halfW;
            inInterior = Math.Abs(x - cx) <= halfW - 1.0;
        }
        return inShape ? (inInterior ? 2 : 1) : 0;
    }
}
