using System.ComponentModel;
using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.Services;
using Legolas.ViewModels;

namespace Legolas.Tests.ViewModels;

public class AnchorEditableTests
{
    private static (SessionState session, MapOverlayViewModel map, LegolasSettings settings)
        BuildSut()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var surveyFlow = new SurveyFlowController(session, settings);
        var optimizer = new AdaptiveRouteOptimizer(new HeldKarpOptimizer(), new NearestNeighbourTwoOptOptimizer());
        var projector = new CoordinateProjector();
        var brushes = new LegolasBrushes(new LegolasColors());
        var map = new MapOverlayViewModel(session, projector, optimizer, surveyFlow, brushes, settings);
        return (session, map, settings);
    }

    // ─── SessionState.IsAnchorEditable truth table ──────────────────────────

    [Fact]
    public void IsAnchorEditable_is_false_before_anchor_is_set()
    {
        var session = new SessionState();
        session.IsAnchorEditable.Should().BeFalse();
    }

    [Fact]
    public void IsAnchorEditable_is_true_after_anchor_is_set_with_no_surveys()
    {
        var session = new SessionState();
        session.HasPlayerPosition = true;
        session.IsAnchorEditable.Should().BeTrue();
    }

    [Fact]
    public void IsAnchorEditable_flips_false_when_first_survey_is_added()
    {
        var session = new SessionState();
        session.HasPlayerPosition = true;
        session.IsAnchorEditable.Should().BeTrue();

        var s = Survey.Create("First", new MetreOffset(1, 1), gridIndex: 0)
            with { ManualOverride = new PixelPoint(50, 50) };
        session.Surveys.Add(new SurveyItemViewModel(s));

        session.IsAnchorEditable.Should().BeFalse("first survey seals the anchor");
    }

    [Fact]
    public void IsAnchorEditable_returns_to_true_when_surveys_cleared()
    {
        var session = new SessionState();
        session.HasPlayerPosition = true;
        var s = Survey.Create("First", new MetreOffset(1, 1), gridIndex: 0);
        session.Surveys.Add(new SurveyItemViewModel(s));
        session.IsAnchorEditable.Should().BeFalse();

        session.ClearSurveys();

        session.IsAnchorEditable.Should().BeTrue();
    }

    [Fact]
    public void IsAnchorEditable_fires_PropertyChanged_when_anchor_is_set()
    {
        var session = new SessionState();
        var seen = new List<string?>();
        session.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        session.HasPlayerPosition = true;

        seen.Should().Contain(nameof(SessionState.IsAnchorEditable));
    }

    [Fact]
    public void IsAnchorEditable_fires_PropertyChanged_when_first_survey_lands()
    {
        var session = new SessionState();
        session.HasPlayerPosition = true;

        var seen = new List<string?>();
        session.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        var s = Survey.Create("First", new MetreOffset(1, 1), gridIndex: 0);
        session.Surveys.Add(new SurveyItemViewModel(s));

        seen.Should().Contain(nameof(SessionState.IsAnchorEditable));
    }

    // ─── MapOverlayViewModel.MoveAnchor ─────────────────────────────────────

    [Fact]
    public void MoveAnchor_updates_PlayerPosition_when_editable()
    {
        var (session, map, _) = BuildSut();
        session.HasPlayerPosition = true;
        session.PlayerPosition = new PixelPoint(100, 100);

        map.MoveAnchor(new PixelPoint(150, 175));

        session.PlayerPosition.X.Should().Be(150);
        session.PlayerPosition.Y.Should().Be(175);
    }

    [Fact]
    public void MoveAnchor_is_noop_before_anchor_is_set()
    {
        var (session, map, _) = BuildSut();
        var initial = session.PlayerPosition;

        map.MoveAnchor(new PixelPoint(999, 999));

        session.PlayerPosition.X.Should().Be(initial.X);
        session.PlayerPosition.Y.Should().Be(initial.Y);
    }

    [Fact]
    public void MoveAnchor_is_noop_after_first_survey_lands()
    {
        var (session, map, _) = BuildSut();
        session.HasPlayerPosition = true;
        session.PlayerPosition = new PixelPoint(100, 100);
        var s = Survey.Create("First", new MetreOffset(1, 1), gridIndex: 0)
            with { ManualOverride = new PixelPoint(120, 120) };
        session.Surveys.Add(new SurveyItemViewModel(s));

        map.MoveAnchor(new PixelPoint(999, 999));

        session.PlayerPosition.X.Should().Be(100, "anchor is sealed once vectors exist");
        session.PlayerPosition.Y.Should().Be(100);
    }
}
