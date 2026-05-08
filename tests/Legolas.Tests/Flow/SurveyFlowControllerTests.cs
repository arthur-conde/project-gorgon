using FluentAssertions;
using Legolas.Domain;
using Legolas.Flow;
using Legolas.ViewModels;

namespace Legolas.Tests.Flow;

public class SurveyFlowControllerTests
{
    private static readonly DateTime FixedTime = new(2026, 5, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Frozen-clock TimeProvider so StartedAt assertions can pin specific instants.
    /// Mirrors the FakeClock used in <c>LegolasReportServiceTests</c>.
    /// </summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) { _now = start; }
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset when) => _now = when;
    }

    private static (SurveyFlowController flow, SessionState session, LegolasSettings settings, List<SurveyTransition> transitions, FakeClock clock) BuildSut()
    {
        var session = new SessionState();
        var settings = new LegolasSettings();
        var clock = new FakeClock(new DateTimeOffset(FixedTime, TimeSpan.Zero));
        var flow = new SurveyFlowController(session, settings, clock);
        var transitions = new List<SurveyTransition>();
        flow.Transitioned += transitions.Add;
        return (flow, session, settings, transitions, clock);
    }

    private static SurveyDetected NewSurvey(string name = "Diamond", int east = 50, int north = 30) =>
        new(FixedTime, name, new MetreOffset(east, north));

    private static void Anchor(SurveyFlowController flow, SessionState session)
    {
        // Pretend the caller has set the projector + player position; controller only
        // needs to know the player position is now valid. After this call the FSM is
        // in Ready (waiting for the first survey to land).
        session.HasPlayerPosition = true;
        flow.ConfirmPlayerPosition();
    }

    [Fact]
    public void InitialState_is_AwaitingPosition()
    {
        var (flow, _, _, _, _) = BuildSut();
        flow.CurrentState.Should().Be(SurveyFlowState.AwaitingPosition);
    }

    [Fact]
    public void ConfirmPlayerPosition_AwaitingPosition_to_Ready()
    {
        var (flow, session, _, transitions, _) = BuildSut();
        session.HasPlayerPosition = true;
        flow.ConfirmPlayerPosition();
        flow.CurrentState.Should().Be(SurveyFlowState.Ready);
        transitions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new SurveyTransition(
                SurveyFlowState.AwaitingPosition, SurveyFlowState.Ready, "ConfirmPlayerPosition"));
    }

    [Fact]
    public void ConfirmPlayerPosition_does_not_stamp_StartedAt()
    {
        // The stamp lives on the Ready→Listening edge (first survey arriving) so it
        // re-fires every cycle. ConfirmPlayerPosition is now purely a state move.
        var (flow, session, _, _, _) = BuildSut();
        session.HasPlayerPosition = true;
        flow.ConfirmPlayerPosition();
        session.StartedAt.Should().BeNull();
    }

    [Fact]
    public void FirstSurvey_in_Ready_transitions_to_Listening_and_stamps_StartedAt()
    {
        var (flow, session, _, transitions, clock) = BuildSut();
        Anchor(flow, session);
        transitions.Clear();
        var stampMoment = new DateTimeOffset(FixedTime.AddMinutes(2), TimeSpan.Zero);
        clock.Set(stampMoment);

        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0)));

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.StartedAt.Should().Be(stampMoment);
        transitions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new SurveyTransition(
                SurveyFlowState.Ready, SurveyFlowState.Listening, "FirstSurvey"));
    }

    [Fact]
    public void SubsequentSurveys_do_not_re_stamp_StartedAt()
    {
        // StartedAt is set once per Listening session. Adding more pins shouldn't
        // overwrite it — ElapsedText would otherwise march backward toward zero.
        var (flow, session, _, _, clock) = BuildSut();
        Anchor(flow, session);
        var first = new DateTimeOffset(FixedTime.AddMinutes(1), TimeSpan.Zero);
        clock.Set(first);
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0)));

        clock.Set(new DateTimeOffset(FixedTime.AddMinutes(5), TimeSpan.Zero));
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Coal", new MetreOffset(10, 0), 1)));

        session.StartedAt.Should().Be(first);
    }

    [Fact]
    public void SecondCycle_after_AutoReset_re_stamps_StartedAt()
    {
        // Regression: previously StartedAt was tied to AwaitingPosition→Listening
        // and never re-fired after auto-reset, so run #2 had StartedAt == CompletedAt
        // (≈ 0s elapsed). The Ready-state redesign means every first-survey arrival
        // stamps fresh, regardless of how many cycles came before.
        var (flow, session, settings, _, clock) = BuildSut();
        settings.AutoResetWhenAllCollected = true;
        Anchor(flow, session);

        // Cycle 1: surveys arrive, all collected, auto-reset returns to Ready.
        var t1Start = new DateTimeOffset(FixedTime.AddMinutes(1), TimeSpan.Zero);
        clock.Set(t1Start);
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        s1.UpdateModel(s1.Model with { Collected = true });

        flow.CurrentState.Should().Be(SurveyFlowState.Ready, "auto-reset lands on Ready");
        session.StartedAt.Should().BeNull("ClearSurveys wiped the cycle-1 stamp");

        // Cycle 2: a fresh survey arrives.
        var t2Start = new DateTimeOffset(FixedTime.AddMinutes(10), TimeSpan.Zero);
        clock.Set(t2Start);
        var s2 = new SurveyItemViewModel(Survey.Create("Coal", new MetreOffset(10, 0), 0));
        session.Surveys.Add(s2);

        flow.CurrentState.Should().Be(SurveyFlowState.Listening);
        session.StartedAt.Should().Be(t2Start, "second cycle re-stamped on first survey");
    }

    [Fact]
    public void NoteSurveyDetected_Ready_surfaces_inventory_without_transition()
    {
        // After the rework: surveys auto-place, so the controller only logs/diagnoses.
        // Ready accepts notifications (the actual Ready→Listening edge is driven by
        // Surveys.CollectionChanged) and surfaces the inventory overlay.
        var (flow, session, _, transitions, _) = BuildSut();
        Anchor(flow, session);
        transitions.Clear();
        session.IsInventoryVisible.Should().BeFalse();

        flow.NoteSurveyDetected(NewSurvey());

        flow.CurrentState.Should().Be(SurveyFlowState.Ready,
            "NoteSurveyDetected itself doesn't transition — that's CollectionChanged's job");
        session.IsInventoryVisible.Should().BeTrue();
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void NoteSurveyDetected_AwaitingPosition_dropped_with_diagnostic()
    {
        var (flow, session, _, transitions, _) = BuildSut();
        flow.NoteSurveyDetected(NewSurvey());
        flow.CurrentState.Should().Be(SurveyFlowState.AwaitingPosition);
        session.LastLogEvent.Should().Contain("ignored").And.Contain("set player position first");
        session.IsInventoryVisible.Should().BeFalse();
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void OptimizeRoute_Listening_to_Gathering_when_surveys_present()
    {
        var (flow, session, _, _, _) = BuildSut();
        Anchor(flow, session);
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0)));
        flow.OptimizeRoute();
        flow.CurrentState.Should().Be(SurveyFlowState.Gathering);
    }

    [Fact]
    public void OptimizeRoute_in_Ready_is_noop()
    {
        // The "non-empty surveys" precondition that used to live in CanOptimize is
        // now structural: Ready means surveys is empty, and OptimizeRoute requires
        // Listening. So calling OptimizeRoute from Ready (anchored, no pins) is a
        // no-op + diagnostic, the same shape as calling it from any other non-Listening
        // state.
        var (flow, session, _, _, _) = BuildSut();
        Anchor(flow, session);
        flow.CurrentState.Should().Be(SurveyFlowState.Ready);
        flow.CanOptimize.Should().BeFalse();

        flow.OptimizeRoute();

        flow.CurrentState.Should().Be(SurveyFlowState.Ready);
        session.LastLogEvent.Should().Contain("OptimizeRoute ignored");
    }

    [Fact]
    public void NoteSurveyDetected_Gathering_dropped_per_position_anchor_constraint()
    {
        var (flow, session, _, transitions, _) = BuildSut();
        Anchor(flow, session);
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0)));
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
        var (flow, session, _, _, _) = BuildSut();
        Anchor(flow, session);
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Coal", new MetreOffset(10, 0), 0)));

        flow.RequestSetPlayerPosition();

        flow.CurrentState.Should().Be(SurveyFlowState.AwaitingPosition);
        session.Surveys.Should().HaveCount(1, "RequestSetPlayerPosition is a re-anchor, not a Reset");
    }

    [Fact]
    public void Reset_clears_surveys_and_returns_to_Ready_when_position_known()
    {
        var (flow, session, _, _, _) = BuildSut();
        Anchor(flow, session);
        session.Surveys.Add(new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0)));
        flow.OptimizeRoute();

        flow.Reset();

        flow.CurrentState.Should().Be(SurveyFlowState.Ready, "HasPlayerPosition is true");
        session.Surveys.Should().BeEmpty();
        session.StartedAt.Should().BeNull("Reset wipes the stamp; next first-survey re-stamps");
    }

    [Fact]
    public void Reset_returns_to_AwaitingPosition_when_no_position()
    {
        var (flow, session, _, _, _) = BuildSut();
        Anchor(flow, session);
        session.HasPlayerPosition = false;

        flow.Reset();

        flow.CurrentState.Should().Be(SurveyFlowState.AwaitingPosition);
    }

    [Fact]
    public void AllCollected_Gathering_to_Done_then_AutoReset_back_to_Ready()
    {
        var (flow, session, settings, transitions, _) = BuildSut();
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

        flow.CurrentState.Should().Be(SurveyFlowState.Ready, "auto-reset returned us to Ready");
        session.Surveys.Should().BeEmpty();
        transitions.Select(t => t.To).Should().ContainInOrder(
            SurveyFlowState.Done, SurveyFlowState.Ready);
    }

    [Fact]
    public void AllCollected_Gathering_to_Done_no_AutoReset_stays_Done()
    {
        var (flow, session, settings, _, _) = BuildSut();
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
        var (flow, session, settings, transitions, _) = BuildSut();
        Anchor(flow, session);
        settings.AutoResetWhenAllCollected = false;
        var s1 = new SurveyItemViewModel(Survey.Create("Diamond", new MetreOffset(50, 30), 0));
        session.Surveys.Add(s1);
        transitions.Clear();

        s1.UpdateModel(s1.Model with { Collected = true });

        flow.CurrentState.Should().Be(SurveyFlowState.Done);
    }

    [Fact]
    public void ConfirmPlayerPosition_from_Ready_is_noop()
    {
        var (flow, session, _, transitions, _) = BuildSut();
        Anchor(flow, session);
        transitions.Clear();
        flow.ConfirmPlayerPosition();
        flow.CurrentState.Should().Be(SurveyFlowState.Ready);
        transitions.Should().BeEmpty();
    }
}
