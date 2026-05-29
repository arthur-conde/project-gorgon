using FluentAssertions;
using Mithril.MapCalibration;
using Mithril.Overlay.Internal;
using Xunit;

namespace Mithril.Overlay.Tests;

/// <summary>
/// Unit-level guards for the thread-safe marker registry:
/// add/remove/update round-trips, per-area filter, idempotent remove,
/// update-unknown is a no-op, input validation (NaN/Infinity/null), and a
/// contention smoke test that two threads adding doesn't lose markers.
/// </summary>
public sealed class WorldOverlayMarkersTests
{
    private sealed record TestStyle(string Name) : IMarkerStyle;

    [Fact]
    public void AddMarker_returns_a_unique_handle_and_appears_in_current_area_snapshot()
    {
        var markers = new WorldOverlayMarkers();
        markers.CurrentArea = "AreaEltibule";

        var style = new TestStyle("a");
        var handle = markers.AddMarker("AreaEltibule", 10, 20, style);

        var snapshot = markers.CurrentAreaMarkers;
        snapshot.Should().ContainSingle().Which.Should().Be(
            new MarkerSnapshot(handle, new WorldCoord(10, 0, 20), style));
    }

    [Fact]
    public void Per_area_filter_hides_markers_registered_for_another_area()
    {
        var markers = new WorldOverlayMarkers();
        var styleA = new TestStyle("A");
        var styleB = new TestStyle("B");
        var handleA = markers.AddMarker("AreaEltibule", 1, 1, styleA);
        var handleB = markers.AddMarker("AreaSerbule", 2, 2, styleB);

        markers.CurrentArea = "AreaSerbule";
        markers.CurrentAreaMarkers.Select(m => m.Handle).Should().ContainSingle().Which.Should().Be(handleB);

        // Re-entering AreaEltibule resurfaces handleA without re-registration.
        markers.CurrentArea = "AreaEltibule";
        markers.CurrentAreaMarkers.Select(m => m.Handle).Should().ContainSingle().Which.Should().Be(handleA);
    }

    [Fact]
    public void Snapshot_is_empty_when_current_area_is_null_or_blank()
    {
        var markers = new WorldOverlayMarkers();
        markers.AddMarker("AreaEltibule", 1, 2, new TestStyle("s"));

        markers.CurrentArea = null;
        markers.CurrentAreaMarkers.Should().BeEmpty();

        markers.CurrentArea = string.Empty;
        markers.CurrentAreaMarkers.Should().BeEmpty();
    }

    [Fact]
    public void RemoveMarker_is_idempotent_and_an_unknown_handle_is_a_silent_no_op()
    {
        var markers = new WorldOverlayMarkers();
        var handle = markers.AddMarker("A", 1, 1, new TestStyle("s"));
        markers.CurrentArea = "A";

        markers.RemoveMarker(handle);
        markers.CurrentAreaMarkers.Should().BeEmpty();

        // Second remove is a silent no-op.
        markers.RemoveMarker(handle);
        markers.CurrentAreaMarkers.Should().BeEmpty();

        // Bogus handle is also a no-op.
        markers.RemoveMarker(new MarkerHandle(Guid.NewGuid()));
    }

    [Fact]
    public void UpdateMarker_moves_existing_markers_and_no_ops_for_unknown_handles()
    {
        var markers = new WorldOverlayMarkers();
        var handle = markers.AddMarker("A", 1, 1, new TestStyle("s"));
        markers.CurrentArea = "A";

        markers.UpdateMarker(handle, 99, -42);
        markers.CurrentAreaMarkers.Single().Should().Match<MarkerSnapshot>(
            m => m.World.X == 99 && m.World.Z == -42);

        // Update an unknown handle — must not throw and must not pollute the
        // registry with a phantom entry.
        markers.UpdateMarker(new MarkerHandle(Guid.NewGuid()), 0, 0);
        markers.CurrentAreaMarkers.Should().HaveCount(1);
    }

    [Fact]
    public void Insertion_order_is_preserved_in_snapshots()
    {
        var markers = new WorldOverlayMarkers();
        markers.CurrentArea = "A";
        var h1 = markers.AddMarker("A", 1, 1, new TestStyle("1"));
        var h2 = markers.AddMarker("A", 2, 2, new TestStyle("2"));
        var h3 = markers.AddMarker("A", 3, 3, new TestStyle("3"));

        markers.CurrentAreaMarkers
            .Select(m => m.Handle)
            .Should().Equal(h1, h2, h3);

        markers.RemoveMarker(h2);
        markers.CurrentAreaMarkers
            .Select(m => m.Handle)
            .Should().Equal(h1, h3);
    }

    [Fact]
    public async Task Concurrent_adds_from_two_threads_do_not_lose_markers()
    {
        var markers = new WorldOverlayMarkers();
        markers.CurrentArea = "A";
        const int perThread = 2000;
        var style = new TestStyle("contention");

        var t1 = Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
                markers.AddMarker("A", i, 0, style);
        });
        var t2 = Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
                markers.AddMarker("A", 0, i, style);
        });
        await Task.WhenAll(t1, t2);

        markers.CurrentAreaMarkers.Should().HaveCount(perThread * 2);
    }
// -- Input validation (M7) --

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddMarker_rejects_null_or_blank_area_key(string? areaKey)
    {
        var markers = new WorldOverlayMarkers();
        var act = () => markers.AddMarker(areaKey!, 0, 0, new TestStyle("s"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddMarker_rejects_null_style()
    {
        var markers = new WorldOverlayMarkers();
        var act = () => markers.AddMarker("A", 0, 0, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    public void AddMarker_rejects_non_finite_coords(double x, double z)
    {
        var markers = new WorldOverlayMarkers();
        var act = () => markers.AddMarker("A", x, z, new TestStyle("s"));
        act.Should().Throw<ArgumentException>().WithMessage("*finite*");
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    public void UpdateMarker_rejects_non_finite_coords(double x, double z)
    {
        var markers = new WorldOverlayMarkers();
        var handle = markers.AddMarker("A", 0, 0, new TestStyle("s"));
        var act = () => markers.UpdateMarker(handle, x, z);
        act.Should().Throw<ArgumentException>().WithMessage("*finite*");
    }
}
