using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Hotkeys;
using Legolas.Services;
using Legolas.ViewModels;
using Mithril.Shared.Hotkeys;

namespace Legolas.Tests.Hotkeys;

public class NudgePinCommandTests
{
    private static (SessionState session, MapOverlayViewModel map, LegolasSettings settings)
        BuildSut()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(settings);
        var map = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes, settings);
        return (session, map, settings);
    }

    private static SurveyItemViewModel SeedSelectedSurveyAt(
        SessionState session, double x, double y)
    {
        var survey = Survey.Create("Test", new MetreOffset(0, 0), gridIndex: 0)
            with { ManualOverride = new PixelPoint(x, y) };
        var vm = new SurveyItemViewModel(survey);
        session.Surveys.Add(vm);
        session.SelectedSurvey = vm;
        return vm;
    }

    [Fact]
    public async Task Up_default_moves_pin_minus_one_y()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        var cmd = new NudgePinUpCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.X.Should().Be(100);
        vm.Model.ManualOverride!.Value.Y.Should().Be(199);
    }

    [Fact]
    public async Task Down_fast_moves_pin_plus_five_y()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        var cmd = new NudgePinDownFastCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.X.Should().Be(100);
        vm.Model.ManualOverride!.Value.Y.Should().BeApproximately(205, 1e-9);
    }

    [Fact]
    public async Task Right_fine_moves_pin_plus_quarter_x()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        var cmd = new NudgePinRightFineCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.X.Should().BeApproximately(100.25, 1e-9);
        vm.Model.ManualOverride!.Value.Y.Should().Be(200);
    }

    [Fact]
    public async Task Left_default_moves_pin_minus_one_x()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        var cmd = new NudgePinLeftCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.X.Should().Be(99);
        vm.Model.ManualOverride!.Value.Y.Should().Be(200);
    }

    [Fact]
    public async Task Override_default_step_changes_resulting_pixel()
    {
        var (session, map, settings) = BuildSut();
        var vm = SeedSelectedSurveyAt(session, 100, 200);
        settings.NudgeStepDefault = 3.0;
        var cmd = new NudgePinUpCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride!.Value.Y.Should().Be(197);
    }

    [Fact]
    public async Task Null_selected_survey_is_noop()
    {
        var (session, map, settings) = BuildSut();
        session.SelectedSurvey = null;
        var cmd = new NudgePinUpCommand(session, map, settings);

        var act = () => cmd.ExecuteAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Selected_survey_without_effective_pixel_is_noop()
    {
        var (session, map, settings) = BuildSut();
        var survey = Survey.Create("Test", new MetreOffset(0, 0), gridIndex: 0);
        // No ManualOverride and no PixelPos → EffectivePixel is null.
        var vm = new SurveyItemViewModel(survey);
        session.Surveys.Add(vm);
        session.SelectedSurvey = vm;

        var cmd = new NudgePinUpCommand(session, map, settings);
        await cmd.ExecuteAsync(CancellationToken.None);

        vm.Model.ManualOverride.Should().BeNull("nudge cannot start from no pixel");
    }

    [Fact]
    public void Negative_step_setting_clamps_to_default()
    {
        var settings = new LegolasSettings();
        settings.NudgeStepDefault = -2.0;
        settings.NudgeStepDefault.Should().Be(1.0, "negative step would silently disable the nudge");
    }

    [Fact]
    public void Zero_step_setting_clamps_to_default()
    {
        var settings = new LegolasSettings();
        settings.NudgeStepFast = 0;
        settings.NudgeStepFast.Should().Be(5.0);
        settings.NudgeStepFine = 0;
        settings.NudgeStepFine.Should().Be(0.25);
    }

    [Fact]
    public void Hotkey_metadata_is_categorised_under_pin_nudge()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);
        cmd.Category.Should().Be("Legolas · Pin Nudge");
        cmd.Id.Should().Be("legolas.pin.nudge.up");
        cmd.DefaultBinding.Should().BeNull("arrow keys collide with in-game movement; user must opt in");
    }

    // ─── Anchor fallback (issue #120) ──────────────────────────────────────
    // When no pin is selected and the anchor is still editable (post-Set
    // Position, pre-first-survey), nudge keys should move the anchor instead
    // of no-op'ing.

    [Fact]
    public async Task Up_with_no_pin_and_editable_anchor_moves_anchor_minus_one_y()
    {
        var (session, map, settings) = BuildSut();
        session.SelectedSurvey = null;
        session.HasPlayerPosition = true;
        session.PlayerPosition = new PixelPoint(400, 300);
        var cmd = new NudgePinUpCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        session.PlayerPosition.X.Should().Be(400);
        session.PlayerPosition.Y.Should().Be(299);
    }

    [Fact]
    public async Task Right_fast_with_no_pin_and_editable_anchor_moves_anchor_plus_five_x()
    {
        var (session, map, settings) = BuildSut();
        session.HasPlayerPosition = true;
        session.PlayerPosition = new PixelPoint(400, 300);
        var cmd = new NudgePinRightFastCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        session.PlayerPosition.X.Should().BeApproximately(405, 1e-9);
        session.PlayerPosition.Y.Should().Be(300);
    }

    [Fact]
    public async Task Down_fine_with_no_pin_and_editable_anchor_moves_anchor_plus_quarter_y()
    {
        var (session, map, settings) = BuildSut();
        session.HasPlayerPosition = true;
        session.PlayerPosition = new PixelPoint(400, 300);
        var cmd = new NudgePinDownFineCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        session.PlayerPosition.X.Should().Be(400);
        session.PlayerPosition.Y.Should().BeApproximately(300.25, 1e-9);
    }

    [Fact]
    public async Task No_pin_and_anchor_unset_is_noop()
    {
        var (session, map, settings) = BuildSut();
        // HasPlayerPosition stays false → IsAnchorEditable is false even though
        // there are no surveys.
        var initial = session.PlayerPosition;
        var cmd = new NudgePinUpCommand(session, map, settings);

        await cmd.ExecuteAsync(CancellationToken.None);

        session.PlayerPosition.X.Should().Be(initial.X);
        session.PlayerPosition.Y.Should().Be(initial.Y);
    }

    [Fact]
    public async Task No_pin_and_anchor_locked_is_noop()
    {
        var (session, map, settings) = BuildSut();
        session.HasPlayerPosition = true;
        session.PlayerPosition = new PixelPoint(400, 300);
        // Adding any survey seals the anchor.
        var survey = Survey.Create("Sealer", new MetreOffset(1, 1), gridIndex: 0)
            with { ManualOverride = new PixelPoint(420, 320) };
        session.Surveys.Add(new SurveyItemViewModel(survey));
        session.SelectedSurvey = null;

        var cmd = new NudgePinUpCommand(session, map, settings);
        await cmd.ExecuteAsync(CancellationToken.None);

        session.PlayerPosition.X.Should().Be(400, "anchor is locked once a survey lands");
        session.PlayerPosition.Y.Should().Be(300);
    }

    [Fact]
    public async Task Selected_pin_takes_precedence_over_editable_anchor()
    {
        // Regression guard: if both fallbacks are eligible (selected pin AND
        // editable anchor), the selected pin wins — keeps the existing nudge
        // semantics intact for users who have a pin selected mid-session.
        var (session, map, settings) = BuildSut();
        session.HasPlayerPosition = true;
        session.PlayerPosition = new PixelPoint(400, 300);
        // Important: anchor is still editable (Surveys.Count == 0).
        // Use a "shadow" pin that lives only in SelectedSurvey, not in Surveys —
        // matches the hotkey-routing precondition without sealing the anchor.
        var shadow = Survey.Create("Shadow", new MetreOffset(0, 0), gridIndex: 99)
            with { ManualOverride = new PixelPoint(100, 200) };
        var shadowVm = new SurveyItemViewModel(shadow);
        session.SelectedSurvey = shadowVm;
        session.IsAnchorEditable.Should().BeTrue();

        var cmd = new NudgePinUpCommand(session, map, settings);
        await cmd.ExecuteAsync(CancellationToken.None);

        session.PlayerPosition.Y.Should().Be(300, "selected pin should be nudged, not the anchor");
    }

    // ─── #139: registration gate ─────────────────────────────────────────────

    [Fact]
    public void IsRegistrable_false_at_idle()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);
        cmd.IsRegistrable.Should().BeFalse("idle session has no pin to nudge");
    }

    [Fact]
    public void IsRegistrable_true_when_anchor_editable()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);

        session.HasPlayerPosition = true;

        session.IsAnchorEditable.Should().BeTrue();
        cmd.IsRegistrable.Should().BeTrue();
    }

    [Fact]
    public void IsRegistrable_true_when_survey_selected()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);

        SeedSelectedSurveyAt(session, 100, 200);

        cmd.IsRegistrable.Should().BeTrue();
    }

    [Fact]
    public void IsRegistrable_false_after_anchor_seals_with_no_selection()
    {
        // Reproduce the in-game pain point: anchor placed, first survey lands,
        // anchor becomes load-bearing (IsAnchorEditable=false), nothing
        // selected — arrow keys should pass through to the game.
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);

        session.HasPlayerPosition = true;
        cmd.IsRegistrable.Should().BeTrue();

        var survey = Survey.Create("First", new MetreOffset(0, 0), gridIndex: 0);
        session.Surveys.Add(new SurveyItemViewModel(survey));
        // Surveys.Count > 0 → IsAnchorEditable flips false; selection is null.

        session.IsAnchorEditable.Should().BeFalse();
        session.SelectedSurvey.Should().BeNull();
        cmd.IsRegistrable.Should().BeFalse();
    }

    [Fact]
    public void IsRegistrable_raises_PropertyChanged_on_session_state_changes()
    {
        var (session, map, settings) = BuildSut();
        var cmd = new NudgePinUpCommand(session, map, settings);

        var fires = 0;
        ((IGatedHotkeyCommand)cmd).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IGatedHotkeyCommand.IsRegistrable)) fires++;
        };

        session.HasPlayerPosition = true;       // → IsAnchorEditable change
        session.SelectedSurvey =
            new SurveyItemViewModel(Survey.Create("S", new MetreOffset(0, 0), gridIndex: 0)); // → SelectedSurvey change

        fires.Should().BeGreaterThanOrEqualTo(2,
            "HotkeyService relies on these signals to flip Win32 registration");
    }
}
