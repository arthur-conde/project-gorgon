using FluentAssertions;
using Legolas.Flow;
using Legolas.ViewModels;

namespace Legolas.Tests.Flow;

public class MotherlodeFlowControllerTests
{
    private static (MotherlodeFlowController flow, SessionState session, List<MotherlodeTransition> transitions) BuildSut()
    {
        var session = new SessionState();
        var flow = new MotherlodeFlowController(session);
        var transitions = new List<MotherlodeTransition>();
        flow.Transitioned += transitions.Add;
        return (flow, session, transitions);
    }

    [Fact]
    public void InitialState_is_Idle()
    {
        var (flow, _, _) = BuildSut();
        flow.CurrentState.Should().Be(MotherlodeFlowState.Idle);
    }

    [Fact]
    public void NoteMeasurement_Idle_to_Measuring()
    {
        var (flow, _, transitions) = BuildSut();
        flow.NoteMeasurement("position1");
        flow.CurrentState.Should().Be(MotherlodeFlowState.Measuring);
        transitions.Should().ContainSingle()
            .Which.From.Should().Be(MotherlodeFlowState.Idle);
    }

    [Fact]
    public void NoteMeasurement_Measuring_no_self_transition()
    {
        var (flow, _, transitions) = BuildSut();
        flow.NoteMeasurement("position1");
        transitions.Clear();
        flow.NoteMeasurement("distance1");
        flow.CurrentState.Should().Be(MotherlodeFlowState.Measuring);
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void OptimizeRoute_Measuring_to_Optimized()
    {
        var (flow, _, _) = BuildSut();
        flow.NoteMeasurement("position1");
        flow.OptimizeRoute();
        flow.CurrentState.Should().Be(MotherlodeFlowState.Optimized);
    }

    [Fact]
    public void OptimizeRoute_Idle_is_noop_with_diagnostic()
    {
        var (flow, session, transitions) = BuildSut();
        flow.OptimizeRoute();
        flow.CurrentState.Should().Be(MotherlodeFlowState.Idle);
        session.LastLogEvent.Should().Contain("OptimizeRoute ignored");
        transitions.Should().BeEmpty();
    }

    [Fact]
    public void NoteMeasurement_Optimized_demotes_to_Measuring()
    {
        // Re-measuring after Optimize is allowed for Motherlode (absolute distances,
        // no anchor invalidation). Differs from Survey mode where Gathering drops
        // new survey events.
        var (flow, _, _) = BuildSut();
        flow.NoteMeasurement("position1");
        flow.OptimizeRoute();
        flow.NoteMeasurement("distance4");
        flow.CurrentState.Should().Be(MotherlodeFlowState.Measuring);
    }

    [Fact]
    public void Reset_returns_to_Idle()
    {
        var (flow, _, transitions) = BuildSut();
        flow.NoteMeasurement("position1");
        flow.OptimizeRoute();
        transitions.Clear();
        flow.Reset();
        flow.CurrentState.Should().Be(MotherlodeFlowState.Idle);
        transitions.Should().ContainSingle()
            .Which.Trigger.Should().Be("Reset");
    }

    [Fact]
    public void Reset_from_Idle_is_noop()
    {
        var (flow, _, transitions) = BuildSut();
        flow.Reset();
        flow.CurrentState.Should().Be(MotherlodeFlowState.Idle);
        transitions.Should().BeEmpty();
    }
}
