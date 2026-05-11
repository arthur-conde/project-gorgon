using FluentAssertions;
using Mithril.Shared.Audio;
using Samwise.Alarms;
using Samwise.Parsing;
using Samwise.State;
using Samwise.Tests;
using Xunit;

namespace Samwise.Tests.Alarms;

public class AlarmServiceTests
{
    private static readonly DateTime Base = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed record Sut(
        AlarmService Service,
        FakeAudioPlaybackSink Sink,
        GardenStateMachine StateMachine,
        FakeTime Time,
        FakeActiveCharacterService ActiveChar,
        SamwiseSettings Settings);

    private static Sut BuildSut(AlarmCollisionBehavior collision = AlarmCollisionBehavior.Replace)
    {
        var cfg = new InMemoryCropConfig();
        var time = new FakeTime(Base);
        var ac = new FakeActiveCharacterService();
        var sm = new GardenStateMachine(cfg, time, activeChar: ac);
        ac.SetActiveCharacter("Hits", "");

        var settings = new SamwiseSettings();
        (settings as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();
        settings.Alarms.Channels[0].Collision = collision;
        settings.Alarms.Rules[PlotStage.Ripe].Enabled = true;
        settings.Alarms.Rules[PlotStage.Thirsty].Enabled = true;

        var sink = new FakeAudioPlaybackSink();
        var service = new AlarmService(sm, settings, sink);
        return new Sut(service, sink, sm, time, ac, settings);
    }

    /// <summary>
    /// Drive a plot from "no state" directly into Ripe by replaying the
    /// log-event sequence used by GardenStateMachineTests.Tier1_StartInteraction.
    /// </summary>
    private static void RipenPlot(Sut s, string plotId, string cropType)
    {
        s.StateMachine.Apply(new SetPetOwner(s.Time.Now.UtcDateTime, plotId));
        s.StateMachine.Apply(new AppearanceLoop(s.Time.Now.UtcDateTime, cropType));
        s.StateMachine.Apply(new UpdateDescription(
            s.Time.Now.UtcDateTime, plotId, cropType, "ripe", "Harvest " + cropType, 1.0));
    }

    /// <summary>
    /// Drive a plot into Thirsty by emitting the Water-Crop action.
    /// </summary>
    private static void ThirstyPlot(Sut s, string plotId, string cropType)
    {
        s.StateMachine.Apply(new SetPetOwner(s.Time.Now.UtcDateTime, plotId));
        s.StateMachine.Apply(new AppearanceLoop(s.Time.Now.UtcDateTime, cropType));
        s.StateMachine.Apply(new UpdateDescription(
            s.Time.Now.UtcDateTime, plotId, cropType, "", "Water " + cropType, 0.5));
    }

    [Fact]
    public void MixChannel_TwoPlotsRipen_BothHandlesPlay_NeitherStopped()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(2);
        s.Sink.Plays[0].Handle.IsPlaying.Should().BeTrue();
        s.Sink.Plays[1].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void ReplaceChannel_SecondPlotRipens_StopsFirstHandle()
    {
        var s = BuildSut(AlarmCollisionBehavior.Replace);

        RipenPlot(s, "1", "Carrot");
        var firstHandle = s.Sink.Plays[0].Handle;
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(2);
        firstHandle.IsPlaying.Should().BeFalse();
        s.Sink.Plays[1].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void SuppressChannel_SecondPlotRipens_DropsAudioButRaisesAlarm()
    {
        var s = BuildSut(AlarmCollisionBehavior.Suppress);
        var triggered = new List<ActiveAlarm>();
        s.Service.AlarmTriggered += (_, a) => triggered.Add(a);

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(1);
        s.Sink.Plays[0].Handle.IsPlaying.Should().BeTrue();
        triggered.Should().HaveCount(2);
    }

    [Fact]
    public void SuppressChannel_FirstHandleFinished_SecondAlarmPlays()
    {
        var s = BuildSut(AlarmCollisionBehavior.Suppress);

        RipenPlot(s, "1", "Carrot");
        s.Sink.Plays[0].Handle.IsPlaying = false;
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(2);
        s.Sink.Plays[1].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void LoopFlag_OnRule_IsPropagatedToSink()
    {
        var s = BuildSut(AlarmCollisionBehavior.Replace);
        s.Settings.Alarms.Rules[PlotStage.Ripe].Loop = true;

        RipenPlot(s, "1", "Carrot");

        s.Sink.Plays.Should().HaveCount(1);
        s.Sink.Plays[0].Loop.Should().BeTrue();
    }
}
