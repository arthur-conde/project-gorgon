using FluentAssertions;
using Legolas.Rendering;
using Mithril.Overlay.Internal;

namespace Legolas.Tests.Rendering;

/// <summary>
/// Smoke test for <see cref="LegolasOverlayDrawerRegistrations.RegisterAll"/>.
/// Step 2 of #835 ships the registration helper but does not call it from
/// <c>Legolas.Module.Activate</c> &#8212; step 3 wires consumers. This test
/// is what guarantees the four drawers are reachable via the renderer's
/// public registration API for the migration window.
/// </summary>
public sealed class LegolasOverlayDrawerRegistrationsTests
{
    [Fact]
    public void RegisterAll_registers_one_drawer_per_marker_style()
    {
        var renderer = new MarkerSceneRenderer();
        renderer.HasAnyDrawer.Should().BeFalse();

        LegolasOverlayDrawerRegistrations_InvokeViaInternals(renderer);

        // Survey + Motherlode + MotherlodeGuidance + Player = 4.
        renderer.DrawerCount.Should().Be(4);
        renderer.HasAnyDrawer.Should().BeTrue();

        // Per-type registration assertions guard against the typo-where-one-
        // type-is-double-registered-and-another-silently-dropped case (count
        // is 4 but only 3 distinct types are wired — Survey pin renders, the
        // player pin is dead, a fourth bogus type takes the slot). Without
        // these the smoke test passes for that bug; with them, the missing
        // type fails its assertion individually.
        renderer.IsRegistered<LegolasSurveyMarkerStyle>().Should().BeTrue(
            "Survey-pin drawer must be wired by RegisterAll");
        renderer.IsRegistered<LegolasMotherlodeMarkerStyle>().Should().BeTrue(
            "Motherlode-pin drawer must be wired by RegisterAll");
        renderer.IsRegistered<LegolasMotherlodeGuidanceMarkerStyle>().Should().BeTrue(
            "Motherlode-guidance drawer must be wired by RegisterAll");
        renderer.IsRegistered<LegolasPlayerMarkerStyle>().Should().BeTrue(
            "Player-anchor drawer must be wired by RegisterAll");
    }

    /// <summary>RegisterAll is internal-to-Legolas.Module (the public surface
    /// belongs to step 3). The test reaches it via the assembly's internal
    /// access — see Mithril.Overlay.csproj's InternalsVisibleTo("Legolas.Tests").
    /// Kept in a separate method so the call site is greppable.</summary>
    private static void LegolasOverlayDrawerRegistrations_InvokeViaInternals(MarkerSceneRenderer renderer)
        => LegolasOverlayDrawerRegistrations.RegisterAll(renderer);
}
