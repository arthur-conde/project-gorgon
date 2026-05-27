using Arda.Abstractions.Logs;
using Arda.World.Player;
using Arda.World.Player.Events;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;

namespace Legolas.Tests.Services;

/// <summary>#506: guided next-spot gates on measurement spots, not the solve minimum.</summary>
public class MotherlodeGuidancePlannerTests
{
  private static MotherlodeSession SessionWithSpot(int slot, int row, double x, double z, int metres, double confidence = 0.8)
    {
        var session = new MotherlodeSession();
        session.LocationSamples.Add(new MotherlodePositionSample(
            new WorldCoord(x, 0, z), MotherlodePositionSource.LogPosition, confidence, DateTimeOffset.UtcNow));
        while (session.Surveys.Count <= slot)
            session.Surveys.Add(MotherlodeSurvey.Create());
        var s = session.Surveys[slot];
        var d = new List<int> { metres };
        session.Surveys[slot] = s with { DistancesByLocation = d };
        return session;
    }

    [Fact]
    public void CountMeasurementSpots_counts_rows_with_distance_not_map_clicks()
    {
        var session = SessionWithSpot(0, 0, 0, 0, 100);
        // Second row exists but no distance yet.
        session.LocationSamples.Add(new MotherlodePositionSample(
            new WorldCoord(30, 0, 0), MotherlodePositionSource.LogPosition, 0.8, DateTimeOffset.UtcNow));

        MotherlodeGuidancePlanner.CountMeasurementSpots(session, 0).Should().Be(1);
    }

    [Fact]
    public void Plan_returns_null_with_zero_spots()
    {
        var session = new MotherlodeSession();
        session.Surveys.Add(MotherlodeSurvey.Create());
        MotherlodeGuidancePlanner.Plan(session, 0, 0, [], []).Should().BeNull();
    }

    [Fact]
    public void One_spot_uses_generic_spread_not_solve()
    {
        var session = SessionWithSpot(0, 0, 0, 0, 500);
        var g = MotherlodeGuidancePlanner.Plan(session, 0, 1, [], []);
        g.Should().NotBeNull();
        g!.Mode.Should().Be(MotherlodeGuidanceMode.GenericSpread);
        g.ToleranceRadiusMetres.Should().BeGreaterThan(0);
        var dist = Math.Sqrt(g.SuggestedWorld.X * g.SuggestedWorld.X + g.SuggestedWorld.Z * g.SuggestedWorld.Z);
        dist.Should().BeApproximately(MotherlodeGuidancePlanner.DefaultSpreadMetres, 2.0);
    }

    [Fact]
    public void Two_spots_use_gdop_optimal_mode()
    {
        var session = SessionWithSpot(0, 0, 0, 0, 500);
        session.LocationSamples.Add(new MotherlodePositionSample(
            new WorldCoord(MotherlodeGuidancePlanner.DefaultSpreadMetres, 0, 0),
            MotherlodePositionSource.LogPosition, 0.8, DateTimeOffset.UtcNow));
        var s = session.Surveys[0];
        session.Surveys[0] = s with { DistancesByLocation = new List<int> { 500, 480 } };

        var g = MotherlodeGuidancePlanner.Plan(session, 0, 2, [], []);
        g.Should().NotBeNull();
        g!.Mode.Should().Be(MotherlodeGuidanceMode.GdopOptimal);
    }

    [Fact]
    public void Coordinator_exposes_guidance_after_one_spot_without_solve()
    {
        var bus = new TestSupport.TestDomainEventBus();
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(new SessionState()), bus);
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        bus.Publish(new PlayerPositionChanged(0, 0, 0, PositionSource.Spawn, new LogLineMetadata(t, t, false)));
        coord.OnUse(t);
        coord.OnDistance(800, t.AddSeconds(2));

        var snap = coord.Snapshot();
        snap.MeasurementSpotCount.Should().Be(1);
        snap.NextSpot.Should().NotBeNull();
        snap.Surveys.Should().ContainSingle().Subject.SolvedWorld.Should().BeNull();
    }

    [Fact]
    public void Three_spots_still_required_for_confident_solve()
    {
        var bus = new TestSupport.TestDomainEventBus();
        var coord = new MotherlodeMeasurementCoordinator(
            new MultilaterationSolver(), new MotherlodeFlowController(new SessionState()), bus);
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        (double X, double Z) target = (200, 300);

        void Measure(double x, double z, DateTimeOffset at)
        {
            bus.Publish(new PlayerPositionChanged(x, 0, z, PositionSource.Spawn, new LogLineMetadata(at, at, false)));
            coord.OnUse(at);
            coord.OnDistance((int)Math.Round(Math.Sqrt((x - target.X) * (x - target.X) + (z - target.Z) * (z - target.Z))), at.AddSeconds(2));
        }

        Measure(0, 0, t);
        Measure(600, 0, t.AddMinutes(2));
        coord.Snapshot().Surveys[0].SolvedWorld.Should().BeNull();

        Measure(0, 600, t.AddMinutes(4));
        coord.Snapshot().Surveys[0].SolvedWorld.Should().NotBeNull();
    }
}
