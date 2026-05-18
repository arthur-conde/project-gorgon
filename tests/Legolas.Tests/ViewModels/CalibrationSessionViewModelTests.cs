using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Legolas.ViewModels;

namespace Legolas.Tests.ViewModels;

public class CalibrationSessionViewModelTests
{
    private static CalibrationReference Ref(string name, double x, double z) =>
        new(name, "NPC", new WorldCoord(x, 0, z));

    [Fact]
    public void Refresh_pulls_references_and_status_from_service()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("Marn", 10, 20), Ref("Yetta", -5, 8) },
        };
        var vm = new CalibrationSessionViewModel(svc);

        vm.References.Should().HaveCount(2);
        vm.StatusText.Should().Contain("not calibrated");
    }

    [Fact]
    public void Placing_references_accumulates_and_gates_solve_at_two()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
        };
        var vm = new CalibrationSessionViewModel(svc);

        vm.CanSolve.Should().BeFalse();

        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(10, 10));
        vm.CanSolve.Should().BeFalse();
        // Auto-advanced to the only remaining unplaced reference.
        vm.SelectedReference.Should().Be(vm.References[1]);

        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(200, 10));
        vm.Placements.Should().HaveCount(2);
        vm.CanSolve.Should().BeTrue();
        vm.SolveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Re_clicking_a_reference_replaces_its_placement_not_adds_a_second()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0) },
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SelectedReference = vm.References[0];

        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(10, 10));
        vm.SelectedReference = vm.References[0]; // re-select (auto-advance cleared it)
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(50, 60));

        vm.Placements.Should().HaveCount(1);
        vm.Placements[0].X.Should().Be(50);
        vm.Placements[0].Y.Should().Be(60);
    }

    [Fact]
    public void Solve_passes_placements_to_service_and_reports_residual()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
            SolveResult = new AreaCalibration(1.0, 0, 0, 0, 2, 0.4),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(0, 0));
        vm.SelectedReference = vm.References[1];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(100, 0));

        vm.SolveCommand.Execute(null);

        svc.LastSolvePairs.Should().HaveCount(2);
        vm.ResultText.Should().Contain("0.4 px");
    }

    [Fact]
    public void Solve_reports_high_residual_warning()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
            SolveResult = new AreaCalibration(1.0, 0, 0, 0, 2, 47.0),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(0, 0));
        vm.SelectedReference = vm.References[1];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(100, 0));

        vm.SolveCommand.Execute(null);

        vm.ResultText.Should().Contain("residual is high");
    }

    [Fact]
    public void Recalibrate_clears_persisted_calibration_and_placements()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(1, 1));

        vm.RecalibrateCommand.Execute(null);

        svc.ClearCalled.Should().BeTrue();
        vm.Placements.Should().BeEmpty();
        vm.CanSolve.Should().BeFalse();
    }

    private sealed class FakeService : IAreaCalibrationService
    {
        public List<CalibrationReference> Refs { get; } = new();
        public AreaCalibration? SolveResult { get; set; }
        public List<(WorldCoord, PixelPoint)>? LastSolvePairs { get; private set; }
        public bool ClearCalled { get; private set; }

        public string? CurrentAreaKey { get; set; }
        public string? CurrentAreaFriendlyName { get; set; }
        public bool IsCurrentAreaCalibrated => CurrentCalibration is not null;
        public AreaCalibration? CurrentCalibration { get; set; }
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Refs;
        public event EventHandler? Changed;

        public void OnAreaEntered(string areaFriendlyName) => Changed?.Invoke(this, EventArgs.Empty);

        public AreaCalibration? CalibrateCurrentArea(IReadOnlyList<(WorldCoord World, PixelPoint Pixel)> placements)
        {
            LastSolvePairs = placements.Select(p => (p.World, p.Pixel)).ToList();
            CurrentCalibration = SolveResult;
            return SolveResult;
        }

        public void ClearCurrentAreaCalibration()
        {
            ClearCalled = true;
            CurrentCalibration = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
