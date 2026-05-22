using FluentAssertions;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Chat.Internal;
using Mithril.WorldSim.Chat.Producers;
using Mithril.WorldSim.Chat.Tests.TestSupport;
using Xunit;

namespace Mithril.WorldSim.Chat.Tests;

public sealed class ChatWorldTests
{
    private static DateTimeOffset Ts(int sec) => new(2026, 5, 19, 21, 0, sec, TimeSpan.FromHours(1));
    private static RawLogLine Line(int sec, string content) => new(Ts(sec), content);

    // ── Drain order ──────────────────────────────────────────────────────

    [Fact]
    public async Task Drains_chat_producer_frames_in_timestamp_order()
    {
        var source = new StubChatLogReplaySource();
        source.PostLive(Line(1, "26-05-19 21:00:01\tone"));
        source.PostLive(Line(2, "26-05-19 21:00:02\ttwo"));
        source.PostLive(Line(3, "26-05-19 21:00:03\tthree"));
        source.Complete();

        var session = new ChatSessionService();
        var producer = new ChatLogProducer(source, session);
        var folder = new RecordingFolder<RawLogLine>();

        var world = new ChatWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        folder.Applied.Select(a => a.Frame.Payload.Line).Should().Equal(
            "26-05-19 21:00:01\tone",
            "26-05-19 21:00:02\ttwo",
            "26-05-19 21:00:03\tthree");
        folder.Applied.Select(a => a.ClockNow).Should().Equal(Ts(1), Ts(2), Ts(3));
        folder.Applied.Select(a => a.ClockFrame).Should().Equal(1L, 2L, 3L);
    }

    // ── Mode transition ──────────────────────────────────────────────────

    [Fact]
    public async Task Replay_drain_then_live_emits_ModeChanged_on_bus()
    {
        var source = new StubChatLogReplaySource();
        source.PostReplay(Line(10, "replay-1"));
        source.PostReplay(Line(11, "replay-2"));
        source.PostLive(Line(12, "live-1"));
        source.Complete();

        var session = new ChatSessionService();
        var producer = new ChatLogProducer(source, session);
        var folder = new RecordingFolder<RawLogLine>();

        var world = new ChatWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var modeChanges = new List<Frame<ModeChanged>>();
        using var _sub = world.Bus.Subscribe<ModeChanged>(modeChanges.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        modeChanges.Should().HaveCount(1);
        modeChanges[0].Payload.From.Should().Be(WorldMode.Replaying);
        modeChanges[0].Payload.To.Should().Be(WorldMode.Live);
        modeChanges[0].Payload.At.Should().Be(Ts(11));   // last replay frame timestamp
        modeChanges[0].Timestamp.Should().Be(Ts(11));

        // Per-frame mode: replay frames dispatched as Replaying, the first
        // live frame dispatched as Live.
        var byPayload = folder.Applied.ToDictionary(a => a.Frame.Payload.Line);
        byPayload["replay-1"].Mode.Should().Be(WorldMode.Replaying);
        byPayload["replay-2"].Mode.Should().Be(WorldMode.Replaying);
        byPayload["live-1"].Mode.Should().Be(WorldMode.Live);

        world.Clock.Mode.Should().Be(WorldMode.Live);
    }

    [Fact]
    public async Task World_with_no_replay_starts_Live_without_emitting_ModeChanged()
    {
        // Live-only stream: every envelope is IsReplay=false. The producer
        // signals ReachedLive on the first emission; the world flips mode
        // before applying the first frame, so no ModeChanged is emitted
        // (Frame == 0 at the flip).
        var source = new StubChatLogReplaySource();
        source.PostLive(Line(1, "live-only"));
        source.Complete();

        var session = new ChatSessionService();
        var producer = new ChatLogProducer(source, session);
        var folder = new RecordingFolder<RawLogLine>();

        var world = new ChatWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var modeChanges = new List<Frame<ModeChanged>>();
        using var sub = world.Bus.Subscribe<ModeChanged>(modeChanges.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        modeChanges.Should().BeEmpty();
        world.Clock.Mode.Should().Be(WorldMode.Live);
        folder.Applied.Single().Mode.Should().Be(WorldMode.Live);
    }

    // ── Scope identification ─────────────────────────────────────────────

    [Fact]
    public async Task Banner_observed_via_world_dispatch_updates_session_service()
    {
        // End-to-end: the producer is registered with the world, the world
        // drains it, and the banner line flows through the dispatch loop.
        // The session service should reflect the scope by world-start completion.
        var source = new StubChatLogReplaySource();
        source.PostReplay(Line(1, "26-05-19 21:00:01\t[Status] preamble"));
        source.PostReplay(Line(2, "26-05-19 21:00:02\t**** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00."));
        source.PostLive(Line(3, "26-05-19 21:00:03\t[Trade] post-banner"));
        source.Complete();

        var session = new ChatSessionService();
        var producer = new ChatLogProducer(source, session);
        var folder = new RecordingFolder<RawLogLine>();

        var world = new ChatWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        session.Current.Should().NotBeNull();
        session.Current!.Server.Should().Be("Laeth");
        session.Current.Character.Should().Be("Emraell");

        folder.Applied.Should().HaveCount(3);
    }

    // ── Registration discipline ─────────────────────────────────────────

    [Fact]
    public void Registering_two_folders_for_the_same_payload_type_throws()
    {
        var world = new ChatWorld();
        world.RegisterFolder(new RecordingFolder<RawLogLine>());

        var act = () => world.RegisterFolder(new RecordingFolder<RawLogLine>());
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered*");
    }

    [Fact]
    public async Task Registrations_after_start_throw()
    {
        var source = new StubChatLogReplaySource();
        source.Complete();

        var session = new ChatSessionService();
        var producer = new ChatLogProducer(source, session);

        var world = new ChatWorld();
        world.RegisterProducer(producer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.StartMerger(cts.Token);

        var folderAct = () => world.RegisterFolder(new RecordingFolder<RawLogLine>());
        folderAct.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot register*after StartAsync*");
    }
}
