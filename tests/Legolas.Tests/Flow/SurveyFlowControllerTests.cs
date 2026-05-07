using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;

namespace Legolas.Tests.Flow;

public class SurveyFlowControllerTests
{
    private static readonly DateTime FixedTime = new(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);

    private static (SurveyFlowController flow, SessionState session, LegolasSettings settings, List<SurveyTransition> transitions) BuildSut()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var flow = new SurveyFlowController(session, settings);
        var transitions = new List<SurveyTransition>();
        flow.Transitioned += transitions.Add;
        return (flow, session, settings, transitions);
    }

    private static SurveyDetected NewSurvey(string name = "Diamond", int east = 50, int north = 30) =>
        new(FixedTime, name, new MetreOffset(east, north));

    private static void Anchor(SurveyFlowController flow, SessionState session)
    {
        // Pretend the caller has set the projector + player position; controller only
        // needs to know the player position is now valid.
        session.HasPlayerPosition = true;
        flow.ConfirmPlayerPosition();
    }

    [Fact]
    public void InitialState_is_AwaitingPosition()
    {
        var (flow, _, _, _) = BuildSut();
        flow.CurrentState.Should().Be(SurveyFlowState.AwaitingPosition);
    }

    [Fact]
    public void ConfirmPlayerPosition_AwaitingPosition_to_Listening()
    {
        var (flow, session, _, transitions) = BuildSut();
        session.HasPlayerPosition = true;
        flow.ConfirmPlayerPosition();
        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        transitions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new SurveyTransition(
                SurveyFlowState.AwaitingPosition, SurveyFlowState.Listening, "ConfirmPlayerPosition"));
    }

    [Fact]
    public void NoteSurveyDetected_Listening_no_transition_surfaces_inventory()
    {
        // After the rework: surveys auto-place, so the controller only logs/diagnoses.
        // Listening is the only state that accepts a survey notification; entering it
        // surfaces the inventory overlay (AutoOverlayCoordinator reacts to visibility).
        var (flow, session, _, transitions) = BuildSut();
        Anchor(flow, session);
        transitions.Clear();
        session.IsInventoryVisible.Should().BeFalse();

        flow.NoteSurveyDetected(NewSurvey());

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.IsInventoryVisible.Should().BeTrue();
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void NoteSurveyDetected_AwaitingPosition_dropped_with_diagnostic()
    {
        var (flow, session, _, transitions) = BuildSut();
        flow.NoteSurveyDetected(NewSurvey());
        flow.CurrentState.Should().Be(SurveyFlowState.AwaitingPosition);
        session.LastLogEvent.Should().Contain("ignored").And.Contain("set player position first");
        session.IsInventoryVisible.Should().BeFalse();
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void OptimizeRoute_Listening_to_Gathering_when_surveys_present()
    {
        var (flow, session, _, _) = BuildSut();
        Anchor(flow, session);
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0)));
        flow.OptimizeRoute();
        flow.CurrentState.Should().Be(SurveyFlowState.Gathering);
    }

    [Fact]
    public void OptimizeRoute_Listening_with_no_surveys_still_transitions()
    {
        // The "are there surveys" check is exposed via CanOptimize; the transition
        // method itself doesn't enforce — callers gate via CanOptimize. Documented
        // shape so tests pin it.
        var (flow, session, _, _) = BuildSut();
        Anchor(flow, session);
        flow.CanOptimize.Should().BeFalse();
        flow.OptimizeRoute();
        flow.CurrentState.Should().Be(SurveyFlowState.Gathering);
    }

    [Fact]
    public void NoteSurveyDetected_Gathering_dropped_per_position_anchor_constraint()
    {
        var (flow, session, _, transitions) = BuildSut();
        Anchor(flow, session);
        flow.OptimizeRoute();
        transitions.Clear();

        flow.NoteSurveyDetected(NewSurvey());

        flow.CurrentState.Should().Be(SurveyFlowState.Gathering);
        session.LastLogEvent.Should().Contain("route in progress");
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void RequestSetPlayerPosition_from_Listening_preserves_surveys()
    {
        var (flow, session, _, _) = BuildSut();
        Anchor(flow, session);
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Coal", new MetreOffset(10, 0), 0)));

        flow.RequestSetPlayerPosition();

        flow.CurrentState.Should().Be(SurveyFlowState.AwaitingPosition);
        session.Surveys.Should().HaveCount(1, "RequestSetPlayerPosition is a re-anchor, not a Reset");
    }

    [Fact]
    public void Reset_clears_surveys_and_returns_to_Listening_when_position_known()
    {
        var (flow, session, _, _) = BuildSut();
        Anchor(flow, session);
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0)));
        flow.OptimizeRoute();

        flow.Reset();

        flow.CurrentState.Should().Be(SurveyFlowState.Listening, "HasPlayerPosition is true");
        session.Surveys.Should().BeEmpty();
    }

    [Fact]
    public void Reset_returns_to_AwaitingPosition_when_no_position()
    {
        var (flow, session, _, _) = BuildSut();
        Anchor(flow, session);
        session.HasPlayerPosition = false;

        flow.Reset();

        flow.CurrentState.Should().Be(SurveyFlowState.AwaitingPosition);
    }

    [Fact]
    public void AllCollected_Gathering_to_Done_then_AutoReset_back_to_Listening()
    {
        var (flow, session, settings, transitions) = BuildSut();
        Anchor(flow, session);
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        var s2 = new SurveyItemViewModel(Survey.Create("Coal", new MetreOffset(10, 0), 1));
        session.Surveys.Add(s1);
        session.Surveys.Add(s2);
        flow.OptimizeRoute();
        transitions.Clear();
        settings.AutoResetWhenAllCollected = true;

        // Marking the last one fires SessionState.AllCollected → controller reacts.
        s1.UpdateModel(s1.Model with { Collected = true });
        s2.UpdateModel(s2.Model with { Collected = true });

        flow.CurrentState.Should().Be(SurveyFlowState.Listening, "auto-reset returned us to Listening");
        session.Surveys.Should().BeEmpty();
        transitions.Select(t => t.To).Should().ContainInOrder(
            SurveyFlowState.Done, SurveyFlowState.Listening);
    }

    [Fact]
    public void AllCollected_Gathering_to_Done_no_AutoReset_stays_Done()
    {
        var (flow, session, settings, _) = BuildSut();
        Anchor(flow, session);
        settings.AutoResetWhenAllCollected = false;
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        flow.OptimizeRoute();

        s1.UpdateModel(s1.Model with { Collected = true });

        flow.CurrentState.Should().Be(SurveyFlowState.Done);
        session.Surveys.Should().HaveCount(1, "no auto-reset, surveys retained for review");
    }

    [Fact]
    public void AllCollected_from_Listening_also_transitions_to_Done()
    {
        // Mark-collected works in Listening too (via hotkey, before optimizing).
        // Preserves pre-FSM behaviour: AllCollected fires a session reset regardless
        // of whether the user optimized first.
        var (flow, session, settings, transitions) = BuildSut();
        Anchor(flow, session);
        settings.AutoResetWhenAllCollected = false;
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        transitions.Clear();

        s1.UpdateModel(s1.Model with { Collected = true });

        flow.CurrentState.Should().Be(SurveyFlowState.Done);
    }

    [Fact]
    public void ConfirmPlayerPosition_from_Listening_is_noop()
    {
        var (flow, session, _, transitions) = BuildSut();
        Anchor(flow, session);
        transitions.Clear();
        flow.ConfirmPlayerPosition();
        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        transitions.Should().BeEmpty();
    }
}
