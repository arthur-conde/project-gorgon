using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.GameState.Movement;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #113 Layers 1/2/4: the read-only projection of the log-driven coordinator —
/// derived <see cref="MotherlodeStage"/>, relative-location headline, and the
/// plain-language confidence pill. Drives the real coordinator (no Application
/// dispatcher in a unit test ⇒ <c>Rebuild</c> runs synchronously).
/// </summary>
public class MotherlodeViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (MotherlodeViewModel vm, MotherlodeMeasurementCoordinator coord,
        FakePlayerPositionTracker pos, FakeAreaCalibrationService area) Build()
    {
        var pos = new FakePlayerPositionTracker();
        var pins = new FakePlayerPinTracker();
        var flow = new MotherlodeFlowController(new SessionState());
        var coord = new MotherlodeMeasurementCoordinator(new MultilaterationSolver(), flow, pos, pins);
        var area = new FakeAreaCalibrationService();
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var vm = new MotherlodeViewModel(coord, optimizer, flow, pins, area);
        return (vm, coord, pos, area);
    }

    private static double D(double x, double z, double tx, double tz) =>
        Math.Sqrt((x - tx) * (x - tx) + (z - tz) * (z - tz));

    private static void Measure(MotherlodeMeasurementCoordinator coord, FakePlayerPositionTracker pos,
        double x, double z, (double X, double Z) target, DateTimeOffset at)
    {
        pos.Push(x, 0, z, PlayerPositionSource.Spawn, at);
        coord.OnUse(at);
        coord.OnDistance((int)Math.Round(D(x, z, target.X, target.Z)), at.AddSeconds(2));
    }

    [Fact]
    public void Stage_starts_at_Measuring()
    {
        var (vm, _, _, _) = Build();
        vm.Stage.Should().Be(MotherlodeStage.Measuring);
        vm.Slots.Should().BeEmpty();
    }

    [Fact]
    public void One_reading_with_no_solve_is_Locating()
    {
        var (vm, coord, pos, _) = Build();

        pos.Push(0, 0, 0, PlayerPositionSource.Spawn, T0);
        coord.OnUse(T0);
        coord.OnDistance(500, T0.AddSeconds(2));

        vm.Stage.Should().Be(MotherlodeStage.Locating);
        vm.LocationCount.Should().Be(1);
        vm.Slots.Should().ContainSingle().Which.HasFix.Should().BeFalse();
    }

    [Fact]
    public void Three_solving_spots_reach_Walk_with_a_relative_headline_and_pill()
    {
        var (vm, coord, pos, area) = Build();
        (double X, double Z) target = (420, -260);
        area.SetReferences(new CalibrationReference("Serbule Keep", "NPC", new WorldCoord(430, 0, -250)));

        Measure(coord, pos, 0, 0, target, T0);
        Measure(coord, pos, 800, 0, target, T0.AddMinutes(2));
        Measure(coord, pos, 0, -800, target, T0.AddMinutes(4));

        vm.Stage.Should().Be(MotherlodeStage.Walk);
        vm.SolvedCount.Should().Be(1);
        var slot = vm.Slots.Should().ContainSingle().Subject;
        slot.HasFix.Should().BeTrue();
        slot.HeadlineText.Should().Contain("Serbule Keep");
        slot.QualityText.Should().NotBeNull();
        // Raw coord/GDOP demoted to the tooltip, not the headline.
        slot.DetailText.Should().Contain("(").And.Contain(")");
        slot.HeadlineText.Should().NotContain("GDOP");
    }

    [Fact]
    public void Collecting_the_only_treasure_reaches_Done()
    {
        var (vm, coord, pos, _) = Build();
        (double X, double Z) target = (420, -260);

        Measure(coord, pos, 0, 0, target, T0);
        Measure(coord, pos, 800, 0, target, T0.AddMinutes(2));
        Measure(coord, pos, 0, -800, target, T0.AddMinutes(4));
        vm.Stage.Should().Be(MotherlodeStage.Walk);

        coord.OnItemCollected("Iron Metal Slab");

        vm.Stage.Should().Be(MotherlodeStage.Done);
        vm.Slots.Should().ContainSingle().Which.Collected.Should().BeTrue();
    }

    [Fact]
    public void Reset_returns_to_Measuring()
    {
        var (vm, coord, pos, _) = Build();
        Measure(coord, pos, 0, 0, (420, -260), T0);

        vm.ResetCommand.Execute(null);

        vm.Stage.Should().Be(MotherlodeStage.Measuring);
        vm.Slots.Should().BeEmpty();
    }
}
