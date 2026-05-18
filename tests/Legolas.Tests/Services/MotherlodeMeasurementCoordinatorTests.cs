using FluentAssertions;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.GameState.Movement;

namespace Legolas.Tests.Services;

/// <summary>
/// #488 — label-agnostic temporal pairing of the use gesture, a position
/// feeder fix, and the ChatLog distance line; collection-gated progression.
/// </summary>
public class MotherlodeMeasurementCoordinatorTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        FakePlayerPinTracker pins) Build()
    {
        var pos = new FakePlayerPositionTracker();
        var pins = new FakePlayerPinTracker();
        var flow = new MotherlodeFlowController(new SessionState());
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), flow, pos, pins);
        return (coord, pos, pins);
    }

    private static double D(double x, double z, double tx, double tz) =>
        Math.Sqrt((x - tx) * (x - tx) + (z - tz) * (z - tz));

    /// <summary>One spot: push a Spawn fix, fire the use, then the distance.</summary>
    private static void Measure(MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        double x, double z, int metres, DateTimeOffset at)
    {
        pos.Push(x, 0, z, PlayerPositionSource.Spawn, at);
        coord.OnUse(at);
        coord.OnDistance(metres, at.AddSeconds(2));
    }

    [Fact]
    public void Three_located_spots_solve_the_treasure_in_world_space()
    {
        var (coord, pos, _) = Build();
        (double X, double Z) target = (420, -260);

        Measure(coord, pos, 0, 0, (int)Math.Round(D(0, 0, target.X, target.Z)), T0);
        Measure(coord, pos, 800, 0, (int)Math.Round(D(800, 0, target.X, target.Z)), T0.AddMinutes(2));
        Measure(coord, pos, 0, -800, (int)Math.Round(D(0, -800, target.X, target.Z)), T0.AddMinutes(4));

        var snap = coord.Snapshot();
        snap.LocationCount.Should().Be(3);
        snap.LocationsWithFix.Should().Be(3);
        var slot = snap.Surveys.Should().ContainSingle().Subject;
        slot.SolvedWorld.Should().NotBeNull();
        var w = slot.SolvedWorld!.Value;
        w.X.Should().BeApproximately(target.X, 3.0);
        w.Z.Should().BeApproximately(target.Z, 3.0);
    }

    [Fact]
    public void Distance_with_no_open_location_is_dropped()
    {
        var (coord, _, _) = Build();

        coord.OnDistance(500, T0);

        coord.Snapshot().LocationCount.Should().Be(0);
        coord.Snapshot().Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Distance_outside_the_use_window_is_dropped()
    {
        var (coord, pos, _) = Build();
        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0);
        coord.OnUse(T0);

        coord.OnDistance(500, T0.AddMinutes(5));   // far past DistanceWindow

        coord.Snapshot().Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Use_with_a_stale_feeder_fix_records_no_position_and_warns()
    {
        var (coord, pos, _) = Build();
        // Fix is 30 min older than the use → beyond MaxFeederGap.
        pos.Push(10, 0, 10, PlayerPositionSource.Spawn, T0);
        coord.OnUse(T0.AddMinutes(30));
        coord.OnDistance(500, T0.AddMinutes(30).AddSeconds(2));

        var snap = coord.Snapshot();
        snap.LocationCount.Should().Be(1);
        snap.LocationsWithFix.Should().Be(0);
        snap.Guidance.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Multiple_maps_at_one_spot_fan_out_to_independent_slots()
    {
        var (coord, pos, _) = Build();
        (double X, double Z) a = (300, 300);
        (double X, double Z) b = (-150, 480);

        void Spot(double x, double z, DateTimeOffset at)
        {
            pos.Push(x, 0, z, PlayerPositionSource.Spawn, at);
            coord.OnUse(at);                                   // map A
            coord.OnDistance((int)Math.Round(D(x, z, a.X, a.Z)), at.AddSeconds(1));
            coord.OnUse(at.AddSeconds(2));                      // map B, same cluster
            coord.OnDistance((int)Math.Round(D(x, z, b.X, b.Z)), at.AddSeconds(3));
        }

        Spot(0, 0, T0);
        Spot(900, 0, T0.AddMinutes(2));
        Spot(0, 900, T0.AddMinutes(4));

        var s = coord.Snapshot().Surveys;
        s.Should().HaveCount(2);
        var w0 = s[0].SolvedWorld!.Value;
        var w1 = s[1].SolvedWorld!.Value;
        w0.X.Should().BeApproximately(a.X, 3.0);
        w0.Z.Should().BeApproximately(a.Z, 3.0);
        w1.X.Should().BeApproximately(b.X, 3.0);
        w1.Z.Should().BeApproximately(b.Z, 3.0);
    }

    [Fact]
    public void Metal_slab_collection_marks_the_next_treasure_collected()
    {
        var (coord, pos, _) = Build();
        (double X, double Z) target = (420, -260);
        Measure(coord, pos, 0, 0, (int)Math.Round(D(0, 0, target.X, target.Z)), T0);
        Measure(coord, pos, 800, 0, (int)Math.Round(D(800, 0, target.X, target.Z)), T0.AddMinutes(2));
        Measure(coord, pos, 0, -800, (int)Math.Round(D(0, -800, target.X, target.Z)), T0.AddMinutes(4));

        coord.OnItemCollected("Good Metal Slab");

        coord.Snapshot().Surveys.Should().ContainSingle()
             .Which.Collected.Should().BeTrue();
    }

    [Fact]
    public void A_map_pin_drop_is_an_accepted_position_feeder()
    {
        var (coord, _, pins) = Build();
        (double X, double Z) target = (100, 100);
        // The pin fake stamps ObservedAt with the real clock, so anchor the
        // use timeline on now (within MaxFeederGap of the pin event).
        var now = DateTimeOffset.UtcNow;

        void PinSpot(double x, double z, DateTimeOffset at)
        {
            pins.Add(x, z, "spot");                 // feeder #2
            coord.OnUse(at);
            coord.OnDistance((int)Math.Round(D(x, z, target.X, target.Z)), at.AddSeconds(2));
        }

        // 40 s apart: > LocationClusterSeconds (new spot each) but each use is
        // still within MaxFeederGap of its pin event.
        PinSpot(0, 0, now);
        PinSpot(400, 0, now.AddSeconds(40));
        PinSpot(0, 400, now.AddSeconds(80));

        var snap = coord.Snapshot();
        snap.LocationsWithFix.Should().Be(3);
        snap.Surveys.Should().ContainSingle()
            .Which.SolvedWorld.Should().NotBeNull();
    }

    [Fact]
    public void Reset_clears_all_state()
    {
        var (coord, pos, _) = Build();
        Measure(coord, pos, 0, 0, 300, T0);

        coord.Reset();

        var snap = coord.Snapshot();
        snap.LocationCount.Should().Be(0);
        snap.Surveys.Should().BeEmpty();
        snap.Guidance.Should().BeNull();
    }
}
