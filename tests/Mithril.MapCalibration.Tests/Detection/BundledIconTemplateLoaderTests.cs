using System.Linq;
using FluentAssertions;
using Mithril.MapCalibration.Detection;
using Mithril.MapCalibration.Detection.Internal;
using Xunit;

namespace Mithril.MapCalibration.Tests.Detection;

public sealed class BundledIconTemplateLoaderTests
{
    [Fact]
    public void Loads_all_four_landmark_types_from_committed_resources()
    {
        var set = BundledIconTemplateLoader.Load(logger: null);

        set.Templates.Should().NotBeEmpty();

        var types = set.Templates.Select(t => t.LandmarkType).Distinct().ToList();
        types.Should().Contain(new[] { "TeleportationPlatform", "MeditationPillar", "Portal", "Npc" });
    }

    [Fact]
    public void Every_template_has_positive_dims_and_matching_buffer_lengths()
    {
        var set = BundledIconTemplateLoader.Load(logger: null);

        foreach (var t in set.Templates)
        {
            t.Gray.Width.Should().BeGreaterThan(0);
            t.Gray.Height.Should().BeGreaterThan(0);
            t.Gray.Pixels.Length.Should().Be(t.Gray.Width * t.Gray.Height);
            t.Alpha.Width.Should().Be(t.Gray.Width);
            t.Alpha.Height.Should().Be(t.Gray.Height);
            t.Alpha.Pixels.Length.Should().Be(t.Gray.Width * t.Gray.Height);
        }
    }

    [Fact]
    public void All_landmark_icons_pivot_at_center()
    {
        var set = BundledIconTemplateLoader.Load(logger: null);

        // The real PG Sprite.m_Pivot for ALL FOUR landmark icons is (0.5, 0.5) —
        // centered — confirmed by re-extracting fresh from the live
        // WindowsPlayer_Data/sharedassets0.assets with classdata.tpk (#916: all
        // four icons pivot=(0.50,0.50)). The #913 gate study's sub-pixel cold
        // solves used these same centered-pivot icons.
        //
        // The earlier spec text (and the synthetic placeholder emitter) asserted
        // telepad/portal anchored at the bottom tip (pivotY ≈ 0). That was wrong
        // about the real authored data — the synthetic teardrops merely *chose*
        // a bottom-tip pivot; the real sprites are centered. The templates carry
        // their own pivots through the manifest, so the engine consumes whatever
        // the real data says rather than assuming a teardrop anchor.
        foreach (var t in set.Templates)
        {
            t.PivotX.Should().BeApproximately(0.5, 0.05, $"{t.Name} pivotX");
            t.PivotY.Should().BeApproximately(0.5, 0.05, $"{t.Name} pivotY");
        }
    }

    [Fact]
    public void PixelSha256_matches_the_committed_blob()   // HARD gate — catches manifest/blob skew
    {
        // The loader verifies pixelSha256 internally and returns empty on
        // mismatch; a populated set therefore already implies the hash matched.
        // Assert it explicitly so a regen mistake (updated .bin but stale hash)
        // turns this test red rather than silently degrading at runtime.
        BundledIconTemplateLoader.PixelSha256Verified(logger: null).Should().BeTrue();
    }
}
