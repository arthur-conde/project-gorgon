using FluentAssertions;
using Mithril.Shared.Audio;
using Mithril.WorldSim;
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
        SamwiseSettings Settings,
        FakePlayerWorld? PlayerWorld);

    private static Sut BuildSut(
        AlarmCollisionBehavior collision = AlarmCollisionBehavior.Replace,
        FakePlayerWorld? playerWorld = null)
    {
        var cfg = new InMemoryCropConfig();
        var time = new FakeTime(Base);
        var ac = new FakeActiveCharacterService();
        var sm = new GardenStateMachine(cfg, time, activeChar: ac, playerWorld: playerWorld);
        ac.SetActiveCharacter("Hits", "");

        var settings = new SamwiseSettings();
        (settings as Mithril.Shared.Settings.IPostLoadInit)?.PostLoadInit();
        settings.Alarms.Channels[0].Collision = collision;
        settings.Alarms.Rules[PlotStage.Ripe].Enabled = true;
        settings.Alarms.Rules[PlotStage.Thirsty].Enabled = true;

        var sink = new FakeAudioPlaybackSink();
        var service = new AlarmService(sm, settings, sink, playerWorld);
        return new Sut(service, sink, sm, time, ac, settings, playerWorld);
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

    [Fact]
    public void PreviewStage_RoutesThroughChannel_AndPropagatesLoop()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].Loop = true;

        s.Service.PreviewStage(PlotStage.Ripe);

        s.Sink.Plays.Should().HaveCount(1);
        s.Sink.Plays[0].Loop.Should().BeTrue();
        s.Sink.Plays[0].CallerId.Should().Be("samwise");
    }

    [Fact]
    public void StopPreview_StopsThePreviewOwnedByThisStage()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Service.PreviewStage(PlotStage.Ripe);
        var handle = s.Sink.Plays[0].Handle;
        handle.IsPlaying.Should().BeTrue();

        s.Service.StopPreview(PlotStage.Ripe);

        handle.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public void SuppressIfStagePlaying_DropsSecondAlarmForSameStage()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].SuppressIfStagePlaying = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(1);
        s.Sink.Plays[0].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void SuppressIfStagePlaying_DoesNotBlockADifferentStageOnSameChannel()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].SuppressIfStagePlaying = true;

        RipenPlot(s, "1", "Carrot");        // Ripe alarm → playing
        ThirstyPlot(s, "2", "Onion");       // Thirsty on same channel — should still play

        s.Sink.Plays.Should().HaveCount(2);
        s.Sink.Plays[1].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void SuppressIfStagePlaying_AllowsNextFireOnceFirstStops()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].SuppressIfStagePlaying = true;

        RipenPlot(s, "1", "Carrot");
        s.Sink.Plays[0].Handle.IsPlaying = false;   // simulate first sound finishing
        RipenPlot(s, "2", "Onion");

        s.Sink.Plays.Should().HaveCount(2);
        s.Sink.Plays[1].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void StopAllPlayback_SilencesAllOwners_ButPreservesFiredAtDedup()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");
        s.Service.ActiveKeys.Should().HaveCount(2);

        s.Service.StopAllPlayback();

        s.Sink.Plays.Should().AllSatisfy(p => p.Handle.IsPlaying.Should().BeFalse());
        s.Service.ActiveKeys.Should().HaveCount(2);   // dedup untouched
    }

    [Fact]
    public void StopPreview_DoesNotAffectRealAlarmsOnSameChannel()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        RipenPlot(s, "1", "Carrot");        // real alarm
        s.Service.PreviewStage(PlotStage.Ripe);  // preview on same channel
        var realHandle = s.Sink.Plays[0].Handle;
        var previewHandle = s.Sink.Plays[1].Handle;

        s.Service.StopPreview(PlotStage.Ripe);

        previewHandle.IsPlaying.Should().BeFalse();
        realHandle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void PreviewStage_SuppressChannel_DroppedWhileAnotherAlarmPlays()
    {
        var s = BuildSut(AlarmCollisionBehavior.Suppress);
        RipenPlot(s, "1", "Carrot");
        s.Sink.Plays.Should().HaveCount(1);

        // Preview should hit the same channel and get suppressed.
        s.Service.PreviewStage(PlotStage.Ripe);

        s.Sink.Plays.Should().HaveCount(1);
    }

    [Fact]
    public void PreviewStage_ReplaceChannel_StopsPriorAlarm()
    {
        var s = BuildSut(AlarmCollisionBehavior.Replace);
        RipenPlot(s, "1", "Carrot");
        var firstHandle = s.Sink.Plays[0].Handle;

        s.Service.PreviewStage(PlotStage.Ripe);

        firstHandle.IsPlaying.Should().BeFalse();
        s.Sink.Plays.Should().HaveCount(2);
        s.Sink.Plays[1].Handle.IsPlaying.Should().BeTrue();
    }

    [Fact]
    public void Replace_Loop_HarvestCurrentOwner_TransfersHandleToSurvivor()
    {
        var s = BuildSut(AlarmCollisionBehavior.Replace);
        s.Settings.Alarms.Rules[PlotStage.Ripe].Loop = true;
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;

        RipenPlot(s, "1", "Carrot");
        var firstHandle = s.Sink.Plays[0].Handle;
        RipenPlot(s, "2", "Onion");
        var secondHandle = s.Sink.Plays[1].Handle;
        firstHandle.IsPlaying.Should().BeFalse();   // sanity: Replace stopped it
        secondHandle.IsPlaying.Should().BeTrue();

        // Harvest plot 2 (the current channel owner). Plot 1 is still Ripe.
        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "2", "SummonedOnion"));

        // Handle keeps playing (transferred to plot 1 — no fresh Play call).
        secondHandle.IsPlaying.Should().BeTrue();
        s.Sink.Plays.Should().HaveCount(2);
    }

    [Fact]
    public void Replace_Loop_HarvestAllSurvivors_FinallyStopsLoop()
    {
        var s = BuildSut(AlarmCollisionBehavior.Replace);
        s.Settings.Alarms.Rules[PlotStage.Ripe].Loop = true;
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");
        var liveHandle = s.Sink.Plays[1].Handle;

        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "2", "SummonedOnion"));
        liveHandle.IsPlaying.Should().BeTrue();   // transferred to plot 1
        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "1", "SummonedCarrot"));
        liveHandle.IsPlaying.Should().BeFalse();  // last survivor resolved
    }

    [Fact]
    public void Suppress_Loop_HarvestCurrentOwner_TransfersHandleToSurvivor()
    {
        var s = BuildSut(AlarmCollisionBehavior.Suppress);
        s.Settings.Alarms.Rules[PlotStage.Ripe].Loop = true;
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");          // suppressed — channel busy
        s.Sink.Plays.Should().HaveCount(1);
        var handle = s.Sink.Plays[0].Handle;

        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "1", "SummonedCarrot"));

        handle.IsPlaying.Should().BeTrue();
        s.Sink.Plays.Should().HaveCount(1);
    }

    [Fact]
    public void Mix_SuppressIfStagePlaying_Loop_HarvestCurrentOwner_TransfersHandleToSurvivor()
    {
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].Loop = true;
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;
        s.Settings.Alarms.Rules[PlotStage.Ripe].SuppressIfStagePlaying = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");          // dropped — same stage already playing
        s.Sink.Plays.Should().HaveCount(1);
        var handle = s.Sink.Plays[0].Handle;

        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "1", "SummonedCarrot"));

        handle.IsPlaying.Should().BeTrue();
        s.Sink.Plays.Should().HaveCount(1);
    }

    [Fact]
    public void Mix_Loop_OwnHandlesEach_HarvestOneOnlyStopsThatHandle()
    {
        // Mix without SuppressIfStagePlaying: every plot already owns its own
        // handle, so resolving one must just stop that one — no transfer needed.
        var s = BuildSut(AlarmCollisionBehavior.Mix);
        s.Settings.Alarms.Rules[PlotStage.Ripe].Loop = true;
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");
        var firstHandle = s.Sink.Plays[0].Handle;
        var secondHandle = s.Sink.Plays[1].Handle;

        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "1", "SummonedCarrot"));

        firstHandle.IsPlaying.Should().BeFalse();
        secondHandle.IsPlaying.Should().BeTrue();
        s.Sink.Plays.Should().HaveCount(2);
    }

    [Fact]
    public void LoopOff_DoesNotTransferHandle()
    {
        // Non-loop alarms shouldn't get promoted — the original one-shot already
        // played, and reusing the handle for a survivor would be surprising.
        var s = BuildSut(AlarmCollisionBehavior.Replace);
        s.Settings.Alarms.Rules[PlotStage.Ripe].Loop = false;
        s.Settings.Alarms.Rules[PlotStage.Ripe].StopOnInteraction = true;

        RipenPlot(s, "1", "Carrot");
        RipenPlot(s, "2", "Onion");
        var liveHandle = s.Sink.Plays[1].Handle;

        s.StateMachine.Apply(new StartInteraction(s.Time.Now.UtcDateTime, "2", "SummonedOnion"));

        liveHandle.IsPlaying.Should().BeFalse();
        s.Sink.Plays.Should().HaveCount(2);
    }

    // ── Call 3 / principle 12 — Mode-gated projection ────────────────────────
    //
    // Under WorldMode.Replaying, AlarmService.Fire must not emit user-facing
    // side effects (audio playback, AlarmTriggered event). Upstream state
    // derivation (the _firedAt dedup write in OnPlotChanged) stays
    // mode-agnostic so a Replaying → Live transition mid-session doesn't
    // re-fire alarms the user already lived through.
    //
    // The original sink-list source is the Call 3 ratification in
    // docs/world-simulator.md §Decisions ratified post-#642 (resolves #676).

    [Fact]
    public void OnPlotChanged_Replaying_DoesNotPlayAudio_AndDoesNotRaiseAlarmTriggered()
    {
        var world = new FakePlayerWorld();
        world.WorldClock.Mode = WorldMode.Replaying;
        var s = BuildSut(playerWorld: world);
        var triggered = new List<ActiveAlarm>();
        s.Service.AlarmTriggered += (_, a) => triggered.Add(a);

        RipenPlot(s, "1", "Carrot");

        s.Sink.Plays.Should().BeEmpty(
            "principle 12 — audio playback is a user-facing projection that must not fire while the world is still draining replayed frames");
        triggered.Should().BeEmpty(
            "the AlarmTriggered event is also a user-facing side effect (consumed by toast/inbox UIs) and is suppressed under Replaying");
    }

    [Fact]
    public void OnPlotChanged_Live_FiresAudioAndAlarmTriggered()
    {
        var world = new FakePlayerWorld();
        world.WorldClock.Mode = WorldMode.Live;
        var s = BuildSut(playerWorld: world);
        var triggered = new List<ActiveAlarm>();
        s.Service.AlarmTriggered += (_, a) => triggered.Add(a);

        RipenPlot(s, "1", "Carrot");

        s.Sink.Plays.Should().HaveCount(1,
            "Live mode is the projection-honest window: side effects fire normally");
        triggered.Should().HaveCount(1);
    }

    [Fact]
    public void OnPlotChanged_Replaying_StillUpdatesFiredAtDedup()
    {
        // State derivation is mode-agnostic — _firedAt is updated even under
        // Replaying so a subsequent same-key transition (e.g., a duplicate
        // log line during replay) does not blast the user when the mode
        // transitions to Live.
        var world = new FakePlayerWorld();
        world.WorldClock.Mode = WorldMode.Replaying;
        var s = BuildSut(playerWorld: world);

        RipenPlot(s, "1", "Carrot");

        s.Service.ActiveKeys.Should().HaveCount(1,
            "OnPlotChanged stamps _firedAt before invoking Fire; that dedup write happens regardless of mode");
    }

    [Fact]
    public void OnPlotChanged_NullPlayerWorld_FiresNormally()
    {
        // Defensive default: when no IPlayerWorld is injected (e.g., partial
        // composition tests or pre-#601 code paths), the _worldClock?.Mode
        // null-conditional treats the world as Live so existing tests aren't
        // broken by the guard.
        var s = BuildSut(playerWorld: null);
        var triggered = new List<ActiveAlarm>();
        s.Service.AlarmTriggered += (_, a) => triggered.Add(a);

        RipenPlot(s, "1", "Carrot");

        s.Sink.Plays.Should().HaveCount(1);
        triggered.Should().HaveCount(1);
    }
}
