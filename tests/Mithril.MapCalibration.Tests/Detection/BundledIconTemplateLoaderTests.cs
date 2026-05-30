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
    public void Pin_icons_pivot_at_bottom_tip()
    {
        var set = BundledIconTemplateLoader.Load(logger: null);

        // Teardrop pins (telepad/portal) are authored anchored at the bottom tip,
        // pivotY ≈ 0 in Unity Y-up convention.
        foreach (var t in set.Templates.Where(t => t.Name is "landmark_telepad" or "landmark_portal"))
        {
            t.PivotY.Should().BeApproximately(0.0, 0.05);
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
