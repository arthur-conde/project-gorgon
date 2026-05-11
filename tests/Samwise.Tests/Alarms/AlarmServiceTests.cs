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

    [Fact]
    public void StopOnInteraction_Replace_SecondPlotOwnsChannel_FirstResolveLeavesSecondPlaying()
    {
        var s = BuildSut(AlarmCollisionBehavior.Replace);
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");
        var firstHandle = s.Sink.Plays[0].Handle;
        var secondHandle = s.Sink.Plays[1].Handle;

        // Resolve plot 1 (transition out of Ripe → Harvested via StartInteraction).
        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "1", "SummonedCarrot"));

        firstHandle.IsPlaying.Should().BeFalse();   // already stopped by Replace
        secondHandle.IsPlaying.Should().BeTrue();   // plot 1's resolve must not stop it
    }

    [Fact]
    public void StopOnInteraction_Mix_OnlyOwnHandleStops()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");
        var firstHandle = s.Sink.Plays[0].Handle;
        var secondHandle = s.Sink.Plays[1].Handle;

        // Resolve plot 1.
        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "1", "SummonedCarrot"));

        firstHandle.IsPlaying.Should().BeFalse();
        secondHandle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void DismissAll_StopsEveryChannelOwner()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");

        s.Service.DismissAll();

        s.Sink.Plays.Should().AllSatisfy(p => p.Handle.IsPlaying.Should().BeFalse());
    }

    [Fact]
    public void SnoozeAll_StopsEveryChannelOwner_AndRecordsSnooze()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);

        RipenPlot(s, "1", "Carrot");
        var firstHandle = s.Sink.Plays[0].Handle;

        s.Service.SnoozeAll();
        firstHandle.IsPlaying.Should().BeFalse();

        // Re-trigger the same Ripe transition for plot 1 — snooze must block the
        // second fire from playing audio (no new entry in Plays).
        RipenPlot(s, "1", "Carrot");
        s.Sink.Plays.Should().HaveCount(1);
    }

    [Fact]
    public void HandleHarvested_StopsAllOwnersForThatPlot()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;
        s.Settings.Alarms.Rules[PlotStage.Thirsty].StopOnInteraction = true;

        // Plot 1 enters Thirsty, then Ripe — two alarms on the same Mix channel.
        ThirstyPlot(s, "1", "Carrot");
        RipenPlot(s, "1", "Carrot");
        // Plot 2 stays Ripe to make sure HandleHarvested doesn't touch it.
        RipenPlot(s, "2", "Onion");

        var thirstyHandle = s.Sink.Plays[0].Handle;
        var ripeHandle = s.Sink.Plays[1].Handle;
        var plot2Handle = s.Sink.Plays[^1].Handle;

        // Build a Plot DTO mirroring the in-state plot 1 to pass to HandleHarvested.
        var plot1 = s.StateMachine.Snapshot()["Hits"]["1"];

        s.Service.HandleHarvested(plot1);

        thirstyHandle.IsPlaying.Should().BeFalse();
        ripeHandle.IsPlaying.Should().BeFalse();
        plot2Handle.IsPlaying.Should().BeTrue();
    }
}
