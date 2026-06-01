using System;
using System.Diagnostics;
using FluentAssertions;
using Mithril.MapCalibration.Capture;
using Mithril.MapCalibration.Capture.Tests.Fixtures;
using Mithril.MapCalibration.Detection;
using Xunit;
using Xunit.Abstractions;

namespace Mithril.MapCalibration.Capture.Tests;

public sealed class TextureRegistrationRefinerTests
{
    private readonly ITestOutputHelper _output;

    public TextureRegistrationRefinerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Refine_locates_an_embedded_texture()
    {
        var tex = SyntheticMap.NoisyTexture(seed: 3, w: 64, h: 64);
        var frame = SyntheticMap.PasteInto(tex, canvasW: 160, canvasH: 120, atX: 40, atY: 30);
        var rect = new TextureRegistrationRefiner().Refine(frame, tex, minScore: 0.5);
        rect.Should().NotBeNull();
        rect!.OriginX.Should().BeCloseTo(40, 3);
        rect.OriginY.Should().BeCloseTo(30, 3);
    }

    /// <summary>
    /// #966 seam guard. The structural fix lives in <see cref="TextureRegistrationRefiner.Refine"/>:
    /// it must route through the DOWNSAMPLING <see cref="MapRectLocator.AutoDetect(GrayImage, GrayImage, double, int)"/>
    /// overload, not the native 3-arg one. The existing perf test exercises
    /// <c>AutoDetect</c> DIRECTLY, and the other refiner test feeds &lt;384px inputs where
    /// downsampling is a no-op — so a regression reverting <c>TextureRegistrationRefiner.cs</c>
    /// to the native overload would pass the whole suite undetected. This test drives the
    /// PRODUCTION SEAM (<c>new TextureRegistrationRefiner().Refine(...)</c>) at live
    /// resolution to pin that the seam itself takes the downsampling path.
    /// </summary>
    [Fact]
    public void Refine_through_the_seam_downsamples_at_live_resolution()
    {
        // Live base texture (~2048×2033) — LARGER than the capture, the regime that hung.
        const int tw = 2048, th = 2033;
        var texture = SyntheticMap.StructuredTexture(seed: 4242, w: tw, h: th);

        // Live capture ~1257×1049 with the map zoomed all the way out so the whole
        // texture renders into it. The texture is ~square, so the render is HEIGHT-bound
        // by the 1049 capture; size it to fit fully (no clipping) at a known origin.
        const int captureW = 1257, captureH = 1049;
        const int padX = 70, padY = 55;
        int rh = captureH - padY - 40;                  // height-bound render
        int rw = (int)Math.Round((double)rh * tw / th); // preserve texture aspect
        var rendered = ImageOps.Resize(texture, rw, rh);

        var shot = new byte[captureW * captureH];
        Array.Fill(shot, (byte)40);
        for (int y = 0; y < rh && (y + padY) < captureH; y++)
            Buffer.BlockCopy(rendered.Pixels, y * rw, shot, (y + padY) * captureW + padX, rw);
        var capture = new GrayImage(captureW, captureH, shot);

        var sw = Stopwatch.StartNew();
        var rect = new TextureRegistrationRefiner().Refine(capture, texture, minScore: 0.3);
        sw.Stop();

        _output.WriteLine($"seam Refine at live resolution: {sw.ElapsedMilliseconds} ms");

        // CORRECTNESS: the embedded texture is located, the result comes back in
        // FULL-capture coordinates (origin/size on the order of the 1257×1049 capture,
        // NOT the 384px working size), and the texture dims are the FULL texture — never
        // the working-resolution copy. The ~4× capture box-average leaves ≲ one working
        // pixel (≈4 full px) of origin quantisation plus the discrete-rung scale slop.
        rect.Should().NotBeNull("the embedded texture must be locatable through the seam");
        rect!.OriginX.Should().BeCloseTo(padX, 35);
        rect.OriginY.Should().BeCloseTo(padY, 35);
        rect.Width.Should().BeGreaterThan(MapRectLocator.DefaultWorkingLongEdgePx,
            "the rect must be unscaled back to full-capture pixels, not left at working resolution");
        rect.TextureWidth.Should().Be(tw);
        rect.TextureHeight.Should().Be(th);

        // SPEED (structural tripwire): at the 384px working resolution the seam's NCC
        // ladder completes in a few seconds even in Debug (measured ≈3.8 s isolated,
        // ≈4.1 s under the full parallel suite on a 16-core box). If the seam reverted to
        // the native 3-arg overload it would run the FULL-resolution ladder (~1–2e12
        // multiply-adds, MINUTES — ≥120 s observed live for #966). The 10 s budget gives
        // ~2.4× headroom over the observed contended working-res cost (so it never flakes
        // on a loaded CI box) while still sitting >12× below the minutes-long native
        // regression — so PASSING this assertion PROVES the seam ran at working resolution,
        // not native, and would unambiguously trip on a revert of TextureRegistrationRefiner.cs.
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000,
            "the seam must take the #966 downsampling path; the native ladder would blow this budget by orders of magnitude");
    }
}
