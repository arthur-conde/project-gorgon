using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// Drawer-dispatch behaviour for <see cref="MarkerSceneRenderer"/>. Verifies
/// that a registered drawer is invoked exactly once per matching marker per
/// tick, an unregistered style is silently skipped (v1 contract), and that
/// type-keying is exact (subclasses are NOT polymorphically routed to a
/// base-class drawer — that's a v2 decision).
/// </summary>
public sealed class MarkerSceneRendererTests
{
    private sealed record StyleA : IMarkerStyle;
    private sealed record StyleB : IMarkerStyle;

    [Fact]
    public void Registered_drawer_is_invoked_once_per_matching_marker()
    {
        var renderer = new MarkerSceneRenderer();
        var calls = new List<PixelPoint>();
        renderer.RegisterDrawer<StyleA>((style, pixel, rt, factory, brushes) => calls.Add(pixel));

        var markers = new List<(PixelPoint, IMarkerStyle)>
        {
            (new PixelPoint(10, 20), new StyleA()),
            (new PixelPoint(30, 40), new StyleA()),
        };

        // Passing nulls for D2D plumbing is fine — the registered drawer
        // never touches them.
        renderer.Render(markers, rt: null!, factory: null!, brushes: null!);

        calls.Should().Equal(new PixelPoint(10, 20), new PixelPoint(30, 40));
    }

    [Fact]
    public void Unregistered_style_is_silently_skipped()
    {
        var renderer = new MarkerSceneRenderer();
        var calls = 0;
        renderer.RegisterDrawer<StyleA>((s, p, rt, f, b) => calls++);

        var markers = new List<(PixelPoint, IMarkerStyle)>
        {
            (new PixelPoint(0, 0), new StyleA()),
            (new PixelPoint(1, 1), new StyleB()), // unregistered — must not throw
        };

        renderer.Render(markers, null!, null!, null!);
        calls.Should().Be(1);
    }

    [Fact]
    public void HasAnyDrawer_reflects_registration_state()
    {
        var renderer = new MarkerSceneRenderer();
        renderer.HasAnyDrawer.Should().BeFalse();
        renderer.RegisterDrawer<StyleA>((_, _, _, _, _) => { });
        renderer.HasAnyDrawer.Should().BeTrue();
    }

    [Fact]
    public void Re_registering_a_style_type_replaces_the_drawer()
    {
        var renderer = new MarkerSceneRenderer();
        var call1 = 0;
        var call2 = 0;
        renderer.RegisterDrawer<StyleA>((_, _, _, _, _) => call1++);
        renderer.RegisterDrawer<StyleA>((_, _, _, _, _) => call2++);

        renderer.Render(new[] { (new PixelPoint(0, 0), (IMarkerStyle)new StyleA()) },
            null!, null!, null!);
        call1.Should().Be(0);
        call2.Should().Be(1);
    }
}
