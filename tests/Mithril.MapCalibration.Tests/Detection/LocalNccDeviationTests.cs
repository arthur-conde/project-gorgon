using System;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class LocalNccDeviationTests
{
    private const int W = 60;
    private const int H = 60;
    private const int Win = 11;

    // High-variance terrain so local NCC is defined everywhere.
    private static float[] NoisyTerrain(int seed)
    {
        var rng = new Random(seed);
        var a = new float[W * H];
        for (int i = 0; i < a.Length; i++) a[i] = rng.Next(0, 256);
        return a;
    }

    private static float Dev(float[] dev, int x, int y) => dev[y * W + x];

    [Fact]
    public void Identical_inputs_match_everywhere()
    {
        var terrain = NoisyTerrain(42);
        var copy = (float[])terrain.Clone();

        _ = LocalNccDeviation.DeviationMap(terrain, copy, W, H, Win, out double meanNcc, addedOnly: false);

        meanNcc.Should().BeGreaterThan(0.99);
    }

    [Fact]
    public void Added_high_variance_patch_deviates_while_matched_terrain_stays_low()
    {
        var terrain = NoisyTerrain(7);
        var screenshot = (float[])terrain.Clone();

        // Stamp an "icon" — a fresh high-variance patch — over a region of the
        // screenshot only. The texture there still shows the original terrain.
        var rng = new Random(99);
        for (int dy = -4; dy <= 4; dy++)
            for (int dx = -4; dx <= 4; dx++)
                screenshot[(30 + dy) * W + (30 + dx)] = rng.Next(0, 256);

        var dev = LocalNccDeviation.DeviationMap(screenshot, terrain, W, H, Win, out _, addedOnly: false);

        Dev(dev, 30, 30).Should().BeGreaterThan(0.5f);   // the added patch deviates
        Dev(dev, 10, 10).Should().BeLessThan(0.2f);      // quiet matched terrain stays low
    }

    [Fact]
    public void AddedOnly_treats_obscured_screenshot_as_match_but_full_mode_flags_it()
    {
        // Texture is detailed terrain; screenshot has that region FLATTENED
        // (fog-of-war / obscured) — the detail is on the texture side, not the
        // screenshot side. addedOnly should treat that as a match (no icon
        // there to detect); full mode flags it as deviation.
        var texture = NoisyTerrain(11);
        var screenshot = (float[])texture.Clone();
        // Patch larger than the NCC window so the window centred at (30,30) is
        // entirely flat — the unambiguous "obscured" case (screenshot smooth,
        // texture detailed).
        for (int dy = -10; dy <= 10; dy++)
            for (int dx = -10; dx <= 10; dx++)
                screenshot[(30 + dy) * W + (30 + dx)] = 128f;   // flat patch

        var devAdded = LocalNccDeviation.DeviationMap(screenshot, texture, W, H, Win, out _, addedOnly: true);
        var devFull = LocalNccDeviation.DeviationMap(screenshot, texture, W, H, Win, out _, addedOnly: false);

        Dev(devAdded, 30, 30).Should().BeApproximately(0.0f, 0.05f);   // obscured → not "added"
        Dev(devFull, 30, 30).Should().BeGreaterThan(0.5f);             // full mode flags the mismatch
    }

    [Fact]
    public void ToGrayFloat_maps_gray_image_to_luma_floats()
    {
        var img = new GrayImage(2, 1, new byte[] { 10, 200 });
        var f = LocalNccDeviation.ToGrayFloat(img);
        f.Should().HaveCount(2);
        f[0].Should().BeApproximately(10f, 0.001f);
        f[1].Should().BeApproximately(200f, 0.001f);
    }
}
