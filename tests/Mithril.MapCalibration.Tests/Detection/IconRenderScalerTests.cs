using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Tests.Fixtures;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

/// <summary>
/// Deterministic seam-closer for the icon-template downscale path
/// (<see cref="IconRenderScaler.ScaledDims"/> / <see cref="IconRenderScaler.RenderSized"/>).
///
/// <para>The only pre-existing scaler coverage,
/// <see cref="SyntheticLargeTemplateEndToEndTests"/> (mithril#923), blits each
/// on-screen icon by pushing the large template through the <em>same</em>
/// <see cref="IconRenderScaler.ScaledDims"/> + <see cref="ImageOps.Resize"/> the
/// detector uses (template-vs-itself, NCC ≈ 1.0). A wrong-but-self-consistent
/// downscale therefore shifts both the template and the on-screen blit together,
/// so that end-to-end test stays green even when the downscale target is wrong —
/// it cannot catch a wrong-size downscale.</para>
///
/// <para>This class pins the downscale geometry directly: the exact
/// <see cref="IconRenderScaler.ScaledDims"/> tuples (computed from the published
/// aspect-preserving formula) and the resulting <see cref="IconTemplate.Gray"/> /
/// <see cref="IconTemplate.Alpha"/> dimensions out of
/// <see cref="IconRenderScaler.RenderSized"/>. A ~1.5× drift in the downscale
/// math makes these dim assertions fail (RED) while the template-vs-itself E2E
/// stays GREEN — closing the seam. A companion detection-layer
/// <see cref="NccTemplateMatch"/> fact (<see cref="Correct_scale_template_correlates_better_than_wrong_scale_against_independent_render"/>)
/// scores the correctly- vs wrongly-downscaled template against an
/// <em>independently rendered</em> small teardrop, proving the right scale is the
/// one that actually correlates with a natively-rasterised on-screen icon.</para>
/// </summary>
public sealed class IconRenderScalerTests
{
    // -- ScaledDims: exact aspect-preserving (max-dim) downscale tuples ---------
    // Formula (verified against IconRenderScaler.ScaledDims):
    //   maxDim = max(width, height)
    //   rw = max(1, width  * target / maxDim)   (integer division)
    //   rh = max(1, height * target / maxDim)

    [Fact]
    public void ScaledDims_square_ish_downscale_matches_formula()
    {
        // 96x128, target 32: maxDim=128 → rw=96*32/128=24, rh=128*32/128=32.
        IconRenderScaler.ScaledDims(96, 128, 32).Should().Be((24, 32));
    }

    [Fact]
    public void ScaledDims_non_trivial_aspect_matches_formula()
    {
        // 140x110, target 32: maxDim=140 → rw=140*32/140=32, rh=110*32/140=25
        // (110*32=3520; 3520/140=25 via integer truncation).
        IconRenderScaler.ScaledDims(140, 110, 32).Should().Be((32, 25));
    }

    [Fact]
    public void ScaledDims_is_noop_at_or_below_target()
    {
        // At target: maxDim equals target, so the dims are returned unchanged.
        IconRenderScaler.ScaledDims(24, 32, 32).Should().Be((24, 32));
        IconRenderScaler.ScaledDims(64, 64, 32).Should().NotBe((64, 64)); // sanity: 64→32 DOES shrink
        // Below the search threshold the scaler never calls ScaledDims, but the
        // pure function is still well-defined: target == maxDim is the no-op case.
        IconRenderScaler.ScaledDims(64, 64, 64).Should().Be((64, 64));
    }

    // -- RenderSized: pinned downscale produces the exact ScaledDims geometry ---

    [Fact]
    public void RenderSized_downscales_large_templates_to_pinned_ScaledDims()
    {
        const int pinned = 32;

        // Large templates (all max-dim ≥ 128 px), so RenderSized must engage.
        var specs = new[]
        {
            new SyntheticMap.IconSpec("landmark_portal", "Portal", 96, 128, 60),
            new SyntheticMap.IconSpec("landmark_telepad", "TeleportationPlatform", 140, 110, 180),
            new SyntheticMap.IconSpec("landmark_npc", "Npc", 130, 130, 220),
        };
        var templates = SyntheticMap.BuildTemplates(specs).Templates;

        foreach (var t in templates)
        {
            Math.Max(t.Gray.Width, t.Gray.Height).Should().BeGreaterThanOrEqualTo(
                128, $"template '{t.Name}' must be large enough to trigger the scaler");
        }

        // Screenshot content is irrelevant when a pinned size is supplied (no NCC
        // sweep runs), so a flat buffer is fine.
        var screenshot = new GrayImage(200, 200, new byte[200 * 200]);

        var scaled = IconRenderScaler.RenderSized(
            screenshot, templates, threshold: 0.5, pinnedSize: pinned);

        scaled.Should().HaveCount(templates.Count);

        for (int i = 0; i < templates.Count; i++)
        {
            var orig = templates[i];
            var (rw, rh) = IconRenderScaler.ScaledDims(orig.Gray.Width, orig.Gray.Height, pinned);

            var s = scaled[i];
            s.Gray.Width.Should().Be(rw, $"'{s.Name}' gray width must equal ScaledDims width");
            s.Gray.Height.Should().Be(rh, $"'{s.Name}' gray height must equal ScaledDims height");
            s.Alpha.Width.Should().Be(rw, $"'{s.Name}' alpha width must equal ScaledDims width");
            s.Alpha.Height.Should().Be(rh, $"'{s.Name}' alpha height must equal ScaledDims height");
        }

        // Spot-check the known tuple so the assertion is anchored to a literal.
        var portal = scaled.First(t => t.Name == "landmark_portal");
        (portal.Gray.Width, portal.Gray.Height).Should().Be((24, 32));
    }

    [Fact]
    public void RenderSized_is_passthrough_when_all_templates_are_small()
    {
        // All ≤ ScaleSearchThresholdPx (64 px): the scaler returns the templates
        // unchanged (the synthetic-fixture path).
        var specs = new[]
        {
            new SyntheticMap.IconSpec("landmark_portal", "Portal", 24, 32, 60),
            new SyntheticMap.IconSpec("landmark_telepad", "TeleportationPlatform", 28, 22, 180),
            new SyntheticMap.IconSpec("landmark_npc", "Npc", 64, 64, 220), // exactly at the cap
        };
        var templates = SyntheticMap.BuildTemplates(specs).Templates;
        var screenshot = new GrayImage(200, 200, new byte[200 * 200]);

        var result = IconRenderScaler.RenderSized(
            screenshot, templates, threshold: 0.5, pinnedSize: 32);

        // Pass-through: same instances, unchanged dims.
        result.Should().BeSameAs(templates);
        for (int i = 0; i < templates.Count; i++)
        {
            result[i].Gray.Width.Should().Be(templates[i].Gray.Width);
            result[i].Gray.Height.Should().Be(templates[i].Gray.Height);
        }
    }

    // -- Detection-layer NCC: the size-sensitive "independent render" angle -----

    [Fact]
    public void Correct_scale_template_correlates_better_than_wrong_scale_against_independent_render()
    {
        // A large source template (max-dim 128 px) that the scaler would downscale.
        const int srcW = 96, srcH = 128, lum = 200;
        const int pinned = 32;
        var srcTemplate = SyntheticMap.BuildTemplates(
            new[] { new SyntheticMap.IconSpec("landmark_portal", "Portal", srcW, srcH, lum) }).Templates[0];

        // Correct downscale = ScaledDims(src, pinned) = (24, 32).
        var (cw, ch) = IconRenderScaler.ScaledDims(srcW, srcH, pinned);
        (cw, ch).Should().Be((24, 32));
        var correctGray = ImageOps.Resize(srcTemplate.Gray, cw, ch);
        var correctAlpha = ImageOps.Resize(srcTemplate.Alpha, cw, ch);

        // Wrong downscale ≈ 1.5× too big = (36, 48).
        int ww = cw * 3 / 2, wh = ch * 3 / 2; // 36, 48
        (ww, wh).Should().Be((36, 48));
        var wrongGray = ImageOps.Resize(srcTemplate.Gray, ww, wh);
        var wrongAlpha = ImageOps.Resize(srcTemplate.Alpha, ww, wh);

        // INDEPENDENT on-screen render: a natively-rasterised teardrop at the
        // correct render size, blitted onto a noisy terrain buffer (so NCC has
        // signal and the search is well-defined). This is NOT produced from the
        // large template — it is the size-sensitive counterpart to the
        // template-vs-itself blit in SyntheticLargeTemplateEndToEndTests.
        const int sceneW = 160, sceneH = 160;
        var scene = SyntheticMap.MakeTexture(sceneW, sceneH, seed: 4321);
        double anchorX = sceneW / 2.0;
        double anchorY = sceneH / 2.0 + ch / 2.0; // teardrop anchored bottom-centre
        SyntheticMap.BlitTeardrop(scene, sceneW, sceneH, anchorX, anchorY, cw, ch, lum);
        var sceneImg = new GrayImage(sceneW, sceneH, scene);

        // Zero threshold so we always get a raw best score for both candidates.
        var correctBest = NccTemplateMatch.FindBest(sceneImg, correctGray, correctAlpha, minScore: 0.0);
        var wrongBest = NccTemplateMatch.FindBest(sceneImg, wrongGray, wrongAlpha, minScore: 0.0);

        correctBest.Should().NotBeNull("the correctly-scaled template must find a best match");
        wrongBest.Should().NotBeNull("the wrongly-scaled template should still report a raw best score");

        // The correctly-downscaled template must out-correlate the oversized one
        // by a clear margin against the independent same-size render.
        correctBest!.Value.Score.Should().BeGreaterThan(
            wrongBest!.Value.Score + 0.05,
            "the render-size-matched template correlates with an independently " +
            "rasterised on-screen icon; a 1.5× oversize downscale does not");
    }
}
