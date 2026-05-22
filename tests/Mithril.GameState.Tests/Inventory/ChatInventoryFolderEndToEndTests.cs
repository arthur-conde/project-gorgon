using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Inventory;
using Mithril.GameState.Inventory.Producers;
using Mithril.Shared.Logging;
using Mithril.WorldSim;
using Mithril.WorldSim.Chat;
using Mithril.WorldSim.Chat.Producers;
using Xunit;

namespace Mithril.GameState.Tests.Inventory;

/// <summary>
/// End-to-end tests for the world-sim inventory split (#602) — the ChatWorld
/// half. Wires <see cref="ChatInventoryFrameProducer"/> →
/// <see cref="ChatWorld"/> → <see cref="ChatInventoryStateService"/> folder
/// → <see cref="IWorldEventBus"/> and asserts the pipeline delivers
/// <see cref="ChatInventoryObserved"/> emissions on the chat world's bus in
/// source order. Includes a replay-determinism test that mirrors the
/// player-side equivalent.
/// </summary>
public sealed class ChatInventoryFolderEndToEndTests
{
    private static DateTimeOffset Ts(int h, int m, int s) =>
        new(2026, 5, 22, h, m, s, TimeSpan.Zero);

    /// <summary>
    /// Equivalent of the internal <c>StubChatLogReplaySource</c> from the
    /// Mithril.WorldSim.Chat.Tests harness — copied here because that test
    /// assembly's <c>InternalsVisibleTo</c> doesn't extend to
    /// Mithril.GameState.Tests. Same contract: posted envelopes carry an
    /// <c>IsReplay</c> bit; <c>ReachedLive</c> flips on the first non-replay
    /// envelope.
    /// </summary>
    private sealed class StubReplaySource : IChatLogReplaySource
    {
        private readonly Channel<LogEnvelope<RawLogLine>> _channel =
            Channel.CreateUnbounded<LogEnvelope<RawLogLine>>(
                new UnboundedChannelOptions { SingleReader = true });

        public void PostReplay(RawLogLine line) =>
            _channel.Writer.TryWrite(new LogEnvelope<RawLogLine>(line, IsReplay: true));

        public void PostLive(RawLogLine line) =>
            _channel.Writer.TryWrite(new LogEnvelope<RawLogLine>(line, IsReplay: false));

        public void Complete() => _channel.Writer.TryComplete();

        public async IAsyncEnumerable<LogEnvelope<RawLogLine>> SubscribeWithReplayMarkerAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var e in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return e;
            }
        }
    }

    [Fact]
    public async Task Replay_envelopes_flow_through_producer_chat_world_folder_to_bus_subscribers()
    {
        var source = new StubReplaySource();
        source.PostReplay(new RawLogLine(Ts(8, 22, 21),
            "26-05-22 08:22:21\t[Status] Egg added to inventory."));
        source.PostReplay(new RawLogLine(Ts(8, 22, 22),
            "26-05-22 08:22:22\t[Status] Guava x5 added to inventory."));
        source.Complete();

        var folder = new ChatInventoryStateService();
        var producer = new ChatInventoryFrameProducer(source);
        var world = new ChatWorld();

        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var observed = new List<Frame<ChatInventoryObserved>>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int expectedCount = 2;
        using var _sub = world.Bus.Subscribe<ChatInventoryObserved>(f =>
        {
            observed.Add(f);
            if (observed.Count >= expectedCount) allReceived.TrySetResult();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = world.StartAsync(cts.Token);
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        observed.Should().HaveCount(2);
        observed[0].Payload.DisplayName.Should().Be("Egg");
        observed[0].Payload.Count.Should().Be(1);
        observed[1].Payload.DisplayName.Should().Be("Guava");
        observed[1].Payload.Count.Should().Be(5);

        // Last-observation lookup matches the most recent fold.
        folder.TryGetLastObservation("Egg", out var c, out _).Should().BeTrue();
        c.Should().Be(1);
        folder.TryGetLastObservation("Guava", out c, out _).Should().BeTrue();
        c.Should().Be(5);
    }

    [Fact]
    public async Task Two_runs_against_identical_chat_replay_emit_identical_frames()
    {
        // Chat-side replay-determinism — symmetric to the player-side
        // PlayerInventoryFolderEndToEndTests.Two_runs equivalent. Per #602
        // acceptance criteria, the chat folder pipeline must be a pure
        // function of the source corpus.
        async Task<List<(string Name, int Count)>> RunOnce()
        {
            var source = new StubReplaySource();
            source.PostReplay(new RawLogLine(Ts(8, 22, 21),
                "26-05-22 08:22:21\t[Status] Egg added to inventory."));
            source.PostReplay(new RawLogLine(Ts(8, 22, 22),
                "26-05-22 08:22:22\t[Status] Guava x5 added to inventory."));
            source.PostReplay(new RawLogLine(Ts(8, 22, 23),
                "26-05-22 08:22:23\t[Status] Egg x3 added to inventory."));
            source.Complete();

            var folder = new ChatInventoryStateService();
            var producer = new ChatInventoryFrameProducer(source);
            var world = new ChatWorld();
            world.RegisterProducer(producer);
            world.RegisterFolder(folder);

            var observed = new List<(string, int)>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const int expectedTotal = 3;
            using var _sub = world.Bus.Subscribe<ChatInventoryObserved>(f =>
            {
                observed.Add((f.Payload.DisplayName, f.Payload.Count));
                if (observed.Count >= expectedTotal) done.TrySetResult();
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var run = world.StartAsync(cts.Token);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            return observed;
        }

        var run1 = await RunOnce();
        var run2 = await RunOnce();
        run2.Should().Equal(run1, "world-sim chat folder pipeline must be a pure function of the source stream");
        run1.Should().Equal(new[]
        {
            ("Egg", 1),
            ("Guava", 5),
            ("Egg", 3),
        });
    }
}
