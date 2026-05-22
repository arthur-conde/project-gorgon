using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Chat;
using Mithril.GameState.Chat.Producers;
using Mithril.Shared.Logging;
using Mithril.WorldSim;
using Mithril.WorldSim.Chat.Producers;
using Xunit;

namespace Mithril.GameState.Tests.Chat;

public sealed class PlayerChatLineProducerTests
{
    private static RawLogLine R(string body, int sec)
    {
        var ts = new DateTimeOffset(2026, 5, 22, 8, 0, sec, TimeSpan.Zero);
        return new RawLogLine(ts, body, Sequence: sec, ReadMonotonicTicks: 0);
    }

    private sealed class StubSource : IChatLogReplaySource
    {
        private readonly Channel<LogEnvelope<RawLogLine>> _ch = Channel.CreateUnbounded<LogEnvelope<RawLogLine>>(
            new UnboundedChannelOptions { SingleReader = true });

        public void PostLive(RawLogLine line) => _ch.Writer.TryWrite(new LogEnvelope<RawLogLine>(line, IsReplay: false));
        public void Complete() => _ch.Writer.TryComplete();

        public async IAsyncEnumerable<LogEnvelope<RawLogLine>> SubscribeWithReplayMarkerAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var e in _ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return e;
        }
    }

    private static async Task<List<Frame<PlayerChatLineFrame>>> Drain(PlayerChatLineProducer producer)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var frames = new List<Frame<PlayerChatLineFrame>>();
        await foreach (var f in producer.SubscribeAsync(cts.Token).WithCancellation(cts.Token))
        {
            frames.Add(f);
        }
        return frames;
    }

    [Fact]
    public async Task Emits_one_frame_per_player_chat_line()
    {
        var source = new StubSource();
        source.PostLive(R("26-05-22 08:00:00\t[Trade] Wizard: WTS Egg", 0));
        source.PostLive(R("26-05-22 08:00:01\t[Local] Other: hello", 1));
        source.Complete();

        var producer = new PlayerChatLineProducer(source);
        var frames = await Drain(producer);

        frames.Should().HaveCount(2);
        frames[0].Payload.Channel.Should().Be("Trade");
        frames[0].Payload.Speaker.Should().Be("Wizard");
        frames[0].Payload.Text.Should().Be("WTS Egg");
        frames[1].Payload.Channel.Should().Be("Local");
    }

    [Fact]
    public async Task Status_lines_drain_at_producer()
    {
        var source = new StubSource();
        source.PostLive(R("26-05-22 08:00:00\t[Status] Egg x5 added to inventory.", 0));
        source.PostLive(R("26-05-22 08:00:01\t[Local] Other: hello", 1));
        source.Complete();

        var producer = new PlayerChatLineProducer(source);
        var frames = await Drain(producer);

        // Only the [Local] line; [Status] was drained by the classifier.
        frames.Should().ContainSingle();
        frames[0].Payload.Channel.Should().Be("Local");
    }

    [Fact]
    public async Task NpcChatter_lines_drain_at_producer()
    {
        var source = new StubSource();
        source.PostLive(R("26-05-22 08:00:00\t[NPC Chatter] Wandering Cow: Moo!", 0));
        source.PostLive(R("26-05-22 08:00:01\t[Local] Other: hello", 1));
        source.Complete();

        var producer = new PlayerChatLineProducer(source);
        var frames = await Drain(producer);

        frames.Should().ContainSingle();
        frames[0].Payload.Channel.Should().Be("Local");
    }

    [Fact]
    public async Task UserCreated_room_routes_to_player_chat()
    {
        var source = new StubSource();
        source.PostLive(R("26-05-22 08:00:00\t[woptraders] Endracos: WTS BWUBGUCH", 0));
        source.Complete();

        var producer = new PlayerChatLineProducer(source);
        var frames = await Drain(producer);

        frames.Should().ContainSingle();
        frames[0].Payload.Channel.Should().Be("woptraders");
        frames[0].Payload.Text.Should().Be("WTS BWUBGUCH");
    }

    [Fact]
    public async Task Continuation_lines_aggregate_into_parent_message()
    {
        // Real chat-log shape: an [Item: …] embedded reference becomes a
        // continuation line with no prefix. The producer aggregates these
        // into the parent message via newline separators.
        var source = new StubSource();
        source.PostLive(R("26-05-22 08:00:00\t[Trade] Endracos: WTS", 0));
        source.PostLive(R("[Item: Bee Lover's Bouquet]", 1));
        source.PostLive(R("26-05-22 08:00:02\t[Local] Other: hello", 2));
        source.Complete();

        var producer = new PlayerChatLineProducer(source);
        var frames = await Drain(producer);

        frames.Should().HaveCount(2);
        frames[0].Payload.Channel.Should().Be("Trade");
        frames[0].Payload.Text.Should().Be("WTS\n[Item: Bee Lover's Bouquet]");
        frames[1].Payload.Channel.Should().Be("Local");
    }

    [Fact]
    public async Task Continuation_line_after_status_does_not_leak_into_next_player_message()
    {
        // A continuation following a Status line is bound to the Status
        // pending message (which is itself dropped at flush time because
        // kind != PlayerChat). The next [Local] line is unaffected.
        var source = new StubSource();
        source.PostLive(R("26-05-22 08:00:00\t[Status] The Citrine is 1731m east.", 0));
        source.PostLive(R("(some unprefixed continuation)", 1));
        source.PostLive(R("26-05-22 08:00:02\t[Local] Other: hello", 2));
        source.Complete();

        var producer = new PlayerChatLineProducer(source);
        var frames = await Drain(producer);

        frames.Should().ContainSingle();
        frames[0].Payload.Channel.Should().Be("Local");
        frames[0].Payload.Text.Should().Be("hello");
    }
}
