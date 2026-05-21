using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.Shared.Logging;
using Mithril.WorldSim.Player.Producers;
using Xunit;

namespace Mithril.WorldSim.Player.Tests.Producers;

public sealed class ClassifiedPlayerLogProducerTests
{
    private static DateTimeOffset Ts(int sec) => new(2026, 1, 1, 12, 0, sec, TimeSpan.Zero);

    [Fact]
    public async Task Yields_one_frame_per_envelope_carrying_envelope_payload_timestamp()
    {
        var stream = new ScriptedClassifiedStream(
            new LogEnvelope<IClassifiedPlayerLogLine>(
                new ScriptedLine(Ts(1), "line-a"), IsReplay: true),
            new LogEnvelope<IClassifiedPlayerLogLine>(
                new ScriptedLine(Ts(2), "line-b"), IsReplay: false));
        stream.Complete();

        var producer = new ClassifiedPlayerLogProducer(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var yielded = new List<Frame<IClassifiedPlayerLogLine>>();
        await foreach (var frame in producer.SubscribeAsync(cts.Token))
        {
            yielded.Add(frame);
        }

        yielded.Should().HaveCount(2);
        yielded[0].Timestamp.Should().Be(Ts(1));
        yielded[0].Payload.Data.Should().Be("line-a");
        yielded[1].Timestamp.Should().Be(Ts(2));
        yielded[1].Payload.Data.Should().Be("line-b");
    }

    [Fact]
    public async Task ReachedLive_completes_on_first_non_replay_envelope()
    {
        var stream = new ScriptedClassifiedStream(
            new LogEnvelope<IClassifiedPlayerLogLine>(
                new ScriptedLine(Ts(1), "replay-1"), IsReplay: true),
            new LogEnvelope<IClassifiedPlayerLogLine>(
                new ScriptedLine(Ts(2), "replay-2"), IsReplay: true));

        var producer = new ClassifiedPlayerLogProducer(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start consuming on a background task so the producer can read its
        // first batch from the stream channel.
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in producer.SubscribeAsync(cts.Token)) { }
        });

        // While we only ever fed replay envelopes, the producer must not
        // signal ReachedLive yet — the L1 contract is "true until the first
        // live envelope; never re-armed."
        await Task.Delay(50);
        producer.ReachedLive.IsCompleted.Should().BeFalse();

        // First live envelope flips the bit.
        stream.Post(new LogEnvelope<IClassifiedPlayerLogLine>(
            new ScriptedLine(Ts(3), "live-1"), IsReplay: false));

        // Wait briefly for the producer's loop to observe the envelope.
        var completed = await Task.WhenAny(producer.ReachedLive, Task.Delay(2000));
        completed.Should().BeSameAs(producer.ReachedLive);

        stream.Complete();
        await consumer;
    }

    [Fact]
    public async Task ReachedLive_completes_when_stream_ends_without_any_live_envelope()
    {
        // Degenerate case — the stream closes mid-replay. We still complete
        // ReachedLive so a world isn't stuck in Replaying after exhaustion.
        var stream = new ScriptedClassifiedStream(
            new LogEnvelope<IClassifiedPlayerLogLine>(
                new ScriptedLine(Ts(1), "only"), IsReplay: true));
        stream.Complete();

        var producer = new ClassifiedPlayerLogProducer(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var _ in producer.SubscribeAsync(cts.Token)) { }

        producer.ReachedLive.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void Priority_is_zero_so_future_producers_can_strictly_outrank_or_undercut()
    {
        var producer = new ClassifiedPlayerLogProducer(new ScriptedClassifiedStream());
        producer.Priority.Should().Be(0);
    }

    // ── Test stream ──────────────────────────────────────────────────────

    private sealed record ScriptedLine(
        DateTimeOffset Timestamp,
        string Data,
        long Sequence = 0,
        long ReadMonotonicTicks = 0,
        string? Raw = null) : IClassifiedPlayerLogLine;

    private sealed class ScriptedClassifiedStream : IClassifiedPlayerLogStream
    {
        private readonly Channel<LogEnvelope<IClassifiedPlayerLogLine>> _channel
            = Channel.CreateUnbounded<LogEnvelope<IClassifiedPlayerLogLine>>(
                new UnboundedChannelOptions { SingleReader = true });

        public ScriptedClassifiedStream(params LogEnvelope<IClassifiedPlayerLogLine>[] initial)
        {
            foreach (var e in initial)
            {
                _channel.Writer.TryWrite(e);
            }
        }

        public void Post(LogEnvelope<IClassifiedPlayerLogLine> envelope)
            => _channel.Writer.TryWrite(envelope);

        public void Complete() => _channel.Writer.TryComplete();

        public async IAsyncEnumerable<LogEnvelope<IClassifiedPlayerLogLine>>
            SubscribeWithReplayMarkerAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var e in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return e;
            }
        }
    }
}
