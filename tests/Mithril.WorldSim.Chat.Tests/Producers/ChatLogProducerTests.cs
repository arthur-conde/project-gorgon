using FluentAssertions;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Chat.Internal;
using Mithril.WorldSim.Chat.Producers;
using Mithril.WorldSim.Chat.Tests.TestSupport;
using Xunit;

namespace Mithril.WorldSim.Chat.Tests.Producers;

public sealed class ChatLogProducerTests
{
    private static DateTimeOffset Ts(int sec) => new(2026, 5, 19, 21, 0, sec, TimeSpan.FromHours(1));

    private static RawLogLine Line(int sec, string content) => new(Ts(sec), content);

    [Fact]
    public async Task Yields_one_frame_per_envelope_carrying_envelope_payload_timestamp()
    {
        var source = new StubChatLogReplaySource();
        source.PostReplay(Line(1, "26-05-19 21:00:01\t[Trade] alpha"));
        source.PostLive(Line(2, "26-05-19 21:00:02\t[Trade] beta"));
        source.Complete();

        var session = new ChatSessionService();
        var producer = new ChatLogProducer(source, session);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var yielded = new List<Frame<RawLogLine>>();
        await foreach (var frame in producer.SubscribeAsync(cts.Token))
        {
            yielded.Add(frame);
        }

        yielded.Should().HaveCount(2);
        yielded[0].Timestamp.Should().Be(Ts(1));
        yielded[0].Payload.Line.Should().Contain("alpha");
        yielded[1].Timestamp.Should().Be(Ts(2));
        yielded[1].Payload.Line.Should().Contain("beta");
    }

    [Fact]
    public async Task ReachedLive_completes_on_first_non_replay_envelope()
    {
        var source = new StubChatLogReplaySource();
        source.PostReplay(Line(1, "replay-1"));
        source.PostReplay(Line(2, "replay-2"));

        var session = new ChatSessionService();
        var producer = new ChatLogProducer(source, session);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in producer.SubscribeAsync(cts.Token)) { }
        });

        // Replay-only — must NOT signal ReachedLive yet.
        await Task.Delay(50);
        producer.ReachedLive.IsCompleted.Should().BeFalse();

        source.PostLive(Line(3, "live-1"));

        var completed = await Task.WhenAny(producer.ReachedLive, Task.Delay(2000));
        completed.Should().BeSameAs(producer.ReachedLive);

        source.Complete();
        await consumer;
    }

    [Fact]
    public async Task ReachedLive_completes_when_source_ends_without_any_live_envelope()
    {
        // Degenerate: replay drain exhausts and the source closes without
        // ever yielding a live envelope. The producer must still complete
        // ReachedLive so a world isn't stuck in Replaying after exhaustion.
        var source = new StubChatLogReplaySource();
        source.PostReplay(Line(1, "only"));
        source.Complete();

        var session = new ChatSessionService();
        var producer = new ChatLogProducer(source, session);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var _ in producer.SubscribeAsync(cts.Token)) { }

        producer.ReachedLive.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Banner_observation_updates_session_service()
    {
        var source = new StubChatLogReplaySource();
        source.PostReplay(Line(1, "26-05-19 21:00:01\t[Trade] pre-banner"));
        source.PostReplay(Line(2, "26-05-19 21:00:02\t**************************************** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00."));
        source.PostLive(Line(3, "26-05-19 21:00:03\t[Trade] post-banner"));
        source.Complete();

        var session = new ChatSessionService();
        var observed = new List<ChatSession>();
        using var sub = session.Subscribe(observed.Add);

        var producer = new ChatLogProducer(source, session);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var _ in producer.SubscribeAsync(cts.Token)) { }

        session.Current.Should().NotBeNull();
        session.Current!.Server.Should().Be("Laeth");
        session.Current.Character.Should().Be("Emraell");
        session.Current.Offset.Should().Be(TimeSpan.FromHours(1));
        session.Current.At.Should().Be(Ts(2));

        observed.Should().HaveCount(1);
        observed[0].Server.Should().Be("Laeth");
    }

    [Fact]
    public async Task Mid_session_relogin_re_anchors_session()
    {
        // PG re-logging into a different character mid-Mithril-run produces
        // a second banner; the session service tracks the latest one.
        var source = new StubChatLogReplaySource();
        source.PostReplay(Line(1, "26-05-19 21:00:01\t**** Logged In As Alice. Server Laeth. Timezone Offset 01:00:00."));
        source.PostLive(Line(2, "26-05-19 21:00:02\t**** Logged In As Bob. Server Cernunnos. Timezone Offset -07:00:00."));
        source.Complete();

        var session = new ChatSessionService();
        var observed = new List<ChatSession>();
        using var sub = session.Subscribe(observed.Add);

        var producer = new ChatLogProducer(source, session);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var _ in producer.SubscribeAsync(cts.Token)) { }

        observed.Should().HaveCount(2);
        observed[0].Character.Should().Be("Alice");
        observed[1].Character.Should().Be("Bob");
        session.Current!.Character.Should().Be("Bob");
        session.Current.Server.Should().Be("Cernunnos");
    }

    [Fact]
    public void Public_constructor_rejects_non_concrete_session_service()
    {
        // The DI extension registers ChatSessionService under IChatSessionService,
        // so the public ctor's downcast is structurally safe. But the producer
        // must still reject a hand-rolled IChatSessionService impl that bypasses
        // the writer-capable concrete type.
        var source = new StubChatLogReplaySource();
        var fake = new FakeSessionService();

        var act = () => new ChatLogProducer(source, fake);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ChatSessionService*");
    }

    private sealed class FakeSessionService : IChatSessionService
    {
        public ChatSession? Current => null;
        public IDisposable Subscribe(Action<ChatSession> handler) => new Sub();
        private sealed class Sub : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void Priority_is_zero_so_future_producers_can_strictly_outrank_or_undercut()
    {
        var session = new ChatSessionService();
        var producer = new ChatLogProducer(new StubChatLogReplaySource(), session);
        producer.Priority.Should().Be(0);
    }
}
