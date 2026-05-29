using FluentAssertions;
using Legolas.Rendering;
using Mithril.Overlay.Internal;
using Xunit;

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
        // DrawerCount is the renderer's internal accessor; only the test
        // assembly (via InternalsVisibleTo on Mithril.Overlay) can read it.
        renderer.DrawerCount.Should().Be(4);
        renderer.HasAnyDrawer.Should().BeTrue();
    }

    /// <summary>RegisterAll is internal-to-Legolas.Module (the public surface
    /// belongs to step 3). The test reaches it via the assembly's internal
    /// access — see Mithril.Overlay.csproj's InternalsVisibleTo("Legolas.Tests").
    /// Kept in a separate method so the call site is greppable.</summary>
    private static void LegolasOverlayDrawerRegistrations_InvokeViaInternals(MarkerSceneRenderer renderer)
        => LegolasOverlayDrawerRegistrations.RegisterAll(renderer);
}
