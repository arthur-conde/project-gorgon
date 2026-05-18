using FluentAssertions;
using Legolas.Domain;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.Shared.Reference;

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
        // The dropped reference stays the active target — visible & nudgeable.
        vm.SelectedReference.Should().Be(vm.References[0]);
        vm.SelectedPlacement.Should().NotBeNull();
        vm.CanNudge.Should().BeTrue();

        // Picking another reference swaps target; the first pin stays put.
        vm.SelectedReference = vm.References[1];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(200, 10));
        vm.Placements.Should().HaveCount(2);
        vm.Placements[0].X.Should().Be(10);   // untouched by the swap
        vm.CanSolve.Should().BeTrue();
        vm.SolveCommand.CanExecute(null).Should().BeTrue();

        // Done deselects: a stray click then warns instead of moving anything.
        vm.DeselectCommand.Execute(null);
        vm.SelectedReference.Should().BeNull();
        vm.SelectedPlacement.Should().BeNull();
        vm.CanNudge.Should().BeFalse();
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(5, 5));
        vm.Placements.Should().HaveCount(2);  // nothing placed/moved
        vm.ClickWarning.Should().NotBeNull();
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
        // Selection persists, so clicking again repositions the same pin
        // (it's the clearly-indicated active target).
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(50, 60));

        vm.Placements.Should().HaveCount(1);   // replaced, not appended
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

    [Fact]
    public void Choosing_an_area_in_the_picker_drives_the_service()
    {
        var svc = new FakeService
        {
            Areas = { new AreaEntry("AreaEltibule", "Eltibule", ""), new AreaEntry("AreaSerbule", "Serbule", "") },
        };
        var vm = new CalibrationSessionViewModel(svc);

        vm.SelectedArea = vm.AvailableAreas.First(a => a.Key == "AreaSerbule");

        svc.SelectedAreaKey.Should().Be("AreaSerbule");
    }

    [Fact]
    public void Dead_click_sets_an_explanatory_warning_instead_of_silently_failing()
    {
        var noArea = new CalibrationSessionViewModel(new FakeService());
        noArea.PlaceSelectedAtCommand.Execute(new PixelPoint(5, 5));
        noArea.ClickWarning.Should().Contain("No area");

        var withRefs = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0) },
        });
        withRefs.PlaceSelectedAtCommand.Execute(new PixelPoint(5, 5)); // nothing selected
        withRefs.ClickWarning.Should().Contain("Pick a landmark");
    }

    [Fact]
    public void Successful_placement_clears_warning_and_arms_nudge_target()
    {
        var vm = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0) },
        });
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(5, 5)); // sets a warning
        vm.ClickWarning.Should().NotBeNull();

        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(40, 60));

        vm.ClickWarning.Should().BeNull();
        vm.SelectedPlacement.Should().NotBeNull();
        vm.SelectedPlacement!.X.Should().Be(40);
    }

    [Fact]
    public void NudgeSelected_moves_the_selected_placement_and_notifies()
    {
        var vm = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0) },
        });
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(100, 100));
        var placed = vm.SelectedPlacement!;

        var xRaised = false;
        placed.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlacedReference.X)) xRaised = true; };

        vm.NudgeSelected(-3, 5);

        placed.Pixel.X.Should().Be(97);
        placed.Pixel.Y.Should().Be(105);
        placed.X.Should().Be(97);
        xRaised.Should().BeTrue();
    }

    [Fact]
    public void NudgeSelected_is_a_noop_with_nothing_selected()
    {
        var vm = new CalibrationSessionViewModel(new FakeService());
        vm.Invoking(v => v.NudgeSelected(1, 1)).Should().NotThrow();
    }

    [Fact]
    public void In_verify_mode_nudge_moves_your_position_and_reprojects_the_pins()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.ToggleTestModeCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new PixelPoint(200, 200)); // your position
        svc.NoteSurvey("Vein", new MetreOffset(East: 0, North: 50));  // pin at (200,150)

        vm.TestPins.Should().ContainSingle();
        vm.TestPins[0].Y.Should().BeApproximately(150, 1e-6);

        vm.CanNudge.Should().BeTrue();                       // armed via test origin, no placement
        vm.NudgeTargetText.Should().Contain("your position");

        vm.NudgeSelected(10, 5); // move "you" right+down

        vm.TestOrigin.Should().Be(new PixelPoint(210, 205));
        // The green pin tracked the moved origin (same offset, new base).
        vm.TestPins[0].X.Should().BeApproximately(210, 1e-6);
        vm.TestPins[0].Y.Should().BeApproximately(155, 1e-6);
    }

    [Fact]
    public void Placement_nudge_still_works_when_not_in_verify_mode()
    {
        var vm = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0) },
        });
        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(100, 100));

        vm.CanNudge.Should().BeTrue();
        vm.NudgeSelected(-3, 4);

        vm.Placements[0].X.Should().Be(97);
        vm.Placements[0].Y.Should().Be(104);
    }

    [Fact]
    public void Active_nudge_target_is_singular_visible_and_named()
    {
        var vm = new CalibrationSessionViewModel(new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0), Ref("B", 100, 0) },
        });

        vm.NudgeTargetText.Should().BeNull(); // nothing armed yet

        vm.SelectedReference = vm.References[0];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(10, 10));
        var a = vm.Placements[0];
        a.IsSelected.Should().BeTrue();
        vm.NudgeTargetText.Should().Contain("A");

        vm.SelectedReference = vm.References[1];
        vm.PlaceSelectedAtCommand.Execute(new PixelPoint(200, 10));
        var b = vm.Placements[1];
        // Exactly one is highlighted — the new one.
        b.IsSelected.Should().BeTrue();
        a.IsSelected.Should().BeFalse();
        vm.NudgeTargetText.Should().Contain("B");

        // Click-selecting the first row in the Placed list moves the highlight.
        vm.SelectedPlacement = a;
        a.IsSelected.Should().BeTrue();
        b.IsSelected.Should().BeFalse();

        // Clearing drops the target entirely.
        vm.ClearPlacementsCommand.Execute(null);
        vm.SelectedPlacement.Should().BeNull();
        vm.NudgeTargetText.Should().BeNull();
        a.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void ViewportClicked_in_test_mode_sets_the_test_origin_not_a_placement()
    {
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            Refs = { Ref("A", 0, 0) },
            CurrentCalibration = new AreaCalibration(1, 0, 0, 0, 2, 0.1),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.SelectedReference = vm.References[0];

        vm.ToggleTestModeCommand.Execute(null);
        vm.TestMode.Should().BeTrue();

        vm.ViewportClickedCommand.Execute(new PixelPoint(120, 80));

        vm.TestOrigin.Should().Be(new PixelPoint(120, 80));
        vm.HasTestOrigin.Should().BeTrue();
        vm.Placements.Should().BeEmpty(); // it was a test-origin click, not a placement
    }

    [Fact]
    public void Test_mode_projects_a_survey_from_the_test_origin_in_the_correct_direction()
    {
        // Identity calibration: scale 1, rotation 0. A purely-north 50m offset
        // must project straight UP (screen-y decreases) from the test origin.
        var svc = new FakeService
        {
            CurrentAreaFriendlyName = "Eltibule",
            CurrentAreaKey = "AreaEltibule",
            CurrentCalibration = new AreaCalibration(1.0, 0.0, 0, 0, 3, 0.2),
        };
        var vm = new CalibrationSessionViewModel(svc);
        vm.ToggleTestModeCommand.Execute(null);
        vm.ViewportClickedCommand.Execute(new PixelPoint(200, 200)); // test origin

        svc.NoteSurvey("Iron Vein", new MetreOffset(East: 0, North: 50));

        vm.TestPins.Should().ContainSingle();
        var pin = vm.TestPins[0];
        pin.Name.Should().Be("Iron Vein");
        pin.X.Should().BeApproximately(200, 1e-6);
        pin.Y.Should().BeApproximately(150, 1e-6); // north → up (smaller y)

        // And east projects to the right.
        svc.NoteSurvey("Slab", new MetreOffset(East: 30, North: 0));
        vm.TestPins[1].X.Should().BeApproximately(230, 1e-6);
        vm.TestPins[1].Y.Should().BeApproximately(200, 1e-6);
    }

    [Fact]
    public void Test_survey_without_origin_or_calibration_warns_instead_of_projecting()
    {
        var noCal = new FakeService { CurrentAreaKey = "AreaEltibule", CurrentAreaFriendlyName = "Eltibule" };
        var vm1 = new CalibrationSessionViewModel(noCal);
        vm1.ToggleTestModeCommand.Execute(null);
        noCal.NoteSurvey("X", new MetreOffset(1, 1));
        vm1.TestPins.Should().BeEmpty();
        vm1.LastSurveyText.Should().Contain("solve a calibration");

        var hasCal = new FakeService
        {
            CurrentAreaKey = "AreaEltibule",
            CurrentAreaFriendlyName = "Eltibule",
            CurrentCalibration = new AreaCalibration(1, 0, 0, 0, 2, 0),
        };
        var vm2 = new CalibrationSessionViewModel(hasCal);
        vm2.ToggleTestModeCommand.Execute(null); // test mode, but no origin clicked yet
        hasCal.NoteSurvey("X", new MetreOffset(1, 1));
        vm2.TestPins.Should().BeEmpty();
        vm2.LastSurveyText.Should().Contain("click where you are");
    }

    [Fact]
    public void Survey_outside_test_mode_still_records_that_it_was_seen()
    {
        var svc = new FakeService
        {
            CurrentAreaKey = "AreaEltibule",
            CurrentAreaFriendlyName = "Eltibule",
            CurrentCalibration = new AreaCalibration(1, 0, 0, 0, 2, 0),
        };
        var vm = new CalibrationSessionViewModel(svc); // test mode OFF

        svc.NoteSurvey("Iron Vein", new MetreOffset(10, -4));

        vm.TestPins.Should().BeEmpty();
        // The survey is NEVER silently swallowed — proves the pipeline is alive.
        vm.LastSurveyText.Should().Contain("Iron Vein");
        vm.LastSurveyText.Should().Contain("Verify");
    }

    private sealed class FakeService : IAreaCalibrationService
    {
        public List<CalibrationReference> Refs { get; } = new();
        public AreaCalibration? SolveResult { get; set; }
        public List<(WorldCoord, PixelPoint)>? LastSolvePairs { get; private set; }
        public bool ClearCalled { get; private set; }

        public List<AreaEntry> Areas { get; } = new();
        public string? SelectedAreaKey { get; private set; }

        public string? CurrentAreaKey { get; set; }
        public string? CurrentAreaFriendlyName { get; set; }
        public bool IsCurrentAreaCalibrated => CurrentCalibration is not null;
        public AreaCalibration? CurrentCalibration { get; set; }
        public IReadOnlyList<CalibrationReference> CurrentAreaReferences => Refs;
        public IReadOnlyList<AreaEntry> AllAreas => Areas;
        public event EventHandler? Changed;

        public void OnAreaEntered(string areaFriendlyName) => Changed?.Invoke(this, EventArgs.Empty);

        public void SelectArea(string areaKey)
        {
            SelectedAreaKey = areaKey;
            CurrentAreaKey = areaKey;
            CurrentAreaFriendlyName = Areas.FirstOrDefault(a => a.Key == areaKey)?.FriendlyName ?? areaKey;
            Changed?.Invoke(this, EventArgs.Empty);
        }

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

        public event EventHandler<CalibrationSurveyObservation>? SurveyObserved;

        public void NoteSurvey(string name, MetreOffset offset) =>
            SurveyObserved?.Invoke(this, new CalibrationSurveyObservation(name, offset));
    }
}
