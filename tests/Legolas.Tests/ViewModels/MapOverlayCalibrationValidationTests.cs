using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;

namespace Legolas.Tests.ViewModels;

/// <summary>
/// #494 — "Validate calibration": projecting known area references as ghost
/// markers via the persisted calibration's <c>ProjectWorld</c>, gated on the
/// area being calibrated, refreshing on calibration change.
/// </summary>
public class MapOverlayCalibrationValidationTests
{
    private static AreaCalibration Cal(double scale) =>
        new(scale, 0.0, 100, 200, 3, 1.5);

    private static (MapOverlayViewModel map, FakeAreaCalibrationService cal, SessionState session) Build()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var cal = new FakeAreaCalibrationService();
        var map = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes,
            settings, pinCalibration: null, positionTracker: null, areaCalibration: cal);
        return (map, cal, session);
    }

    private static CalibrationReference Ref(string name, double x, double z) =>
        new(name, "Landmark", new WorldCoord(x, 0, z));

    [Fact]
    public void Toggle_on_projects_every_reference_via_ProjectWorld()
    {
        var (map, cal, session) = Build();
        var calibration = Cal(2.0);
        cal.SetReferences(Ref("Statue", 10, 5), Ref("Well", -4, 12));
        cal.SetCalibration(calibration);

        map.IsCurrentAreaCalibrated.Should().BeTrue();
        map.ToggleCalibrationValidationCommand.CanExecute(null).Should().BeTrue();

        map.ToggleCalibrationValidationCommand.Execute(null);

        map.ShowCalibrationGhosts.Should().BeTrue();
        session.IsMapVisible.Should().BeTrue("the overlay must be up for the ghosts to be visible");
        map.CalibrationGhosts.Should().HaveCount(2);
        map.CalibrationGhosts[0].Name.Should().Be("Statue");
        map.CalibrationGhosts[0].Pixel.Should().Be(calibration.ProjectWorld(new WorldCoord(10, 0, 5)));
        map.CalibrationGhosts[1].Pixel.Should().Be(calibration.ProjectWorld(new WorldCoord(-4, 0, 12)));
        map.CalibrationValidationStatus.Should().Contain("2 known").And.Contain("not accuracy");
    }

    [Fact]
    public void Toggle_off_clears_the_ghosts_and_status()
    {
        var (map, cal, _) = Build();
        cal.SetReferences(Ref("Statue", 10, 5));
        cal.SetCalibration(Cal(2.0));

        map.ToggleCalibrationValidationCommand.Execute(null);
        map.CalibrationGhosts.Should().NotBeEmpty();

        map.ToggleCalibrationValidationCommand.Execute(null);

        map.ShowCalibrationGhosts.Should().BeFalse();
        map.CalibrationGhosts.Should().BeEmpty();
        map.CalibrationValidationStatus.Should().BeEmpty();
    }

    [Fact]
    public void Uncalibrated_area_disables_the_command_and_yields_no_ghosts()
    {
        var (map, _, _) = Build();   // FakeAreaCalibrationService with no calibration set

        map.IsCurrentAreaCalibrated.Should().BeFalse();
        map.ToggleCalibrationValidationCommand.CanExecute(null).Should().BeFalse();

        // Even if invoked directly (CanExecute is advisory), there is nothing
        // to project, so no ghosts appear.
        map.ToggleCalibrationValidationCommand.Execute(null);
        map.CalibrationGhosts.Should().BeEmpty();
    }

    [Fact]
    public void Recalibration_while_showing_reprojects_the_ghosts()
    {
        var (map, cal, _) = Build();
        cal.SetReferences(Ref("Statue", 10, 5));
        cal.SetCalibration(Cal(2.0));
        map.ToggleCalibrationValidationCommand.Execute(null);
        var before = map.CalibrationGhosts[0].Pixel;

        cal.SetCalibration(Cal(4.0));   // re-solved at a different scale → Changed

        map.CalibrationGhosts.Should().HaveCount(1);
        map.CalibrationGhosts[0].Pixel.Should().NotBe(before);
        map.CalibrationGhosts[0].Pixel.Should().Be(Cal(4.0).ProjectWorld(new WorldCoord(10, 0, 5)));
    }

    [Fact]
    public void Losing_calibration_while_showing_drops_the_overlay()
    {
        var (map, cal, _) = Build();
        cal.SetReferences(Ref("Statue", 10, 5));
        cal.SetCalibration(Cal(2.0));
        map.ToggleCalibrationValidationCommand.Execute(null);
        map.ShowCalibrationGhosts.Should().BeTrue();

        cal.SetCalibration(null);   // calibration cleared

        map.ShowCalibrationGhosts.Should().BeFalse();
        map.CalibrationGhosts.Should().BeEmpty();
        map.ToggleCalibrationValidationCommand.CanExecute(null).Should().BeFalse();
    }
}
