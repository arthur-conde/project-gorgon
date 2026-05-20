using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests.Logging;

/// <summary>
/// L0.5 (#556) instance-classifier tests. Drives <see cref="PlayerLogClassifier"/>
/// directly (without the splitter) and asserts the unified pipe's
/// canonical-ordering and replay-marker invariants. The line-local
/// classify function itself is covered by
/// <see cref="PlayerLogLineClassifierTests"/>; the end-to-end pipeline
/// (classifier + splitter wired together) is covered by
/// <see cref="PlayerLogPipelineTests"/>.
/// </summary>
public sealed class PlayerLogClassifierTests
{
    [Fact]
    public async Task Unified_pipe_yields_every_classified_line_in_source_Sequence_order()
    {
        // Feed a mixed LocalPlayer / SystemSignal / Combat batch and verify
        // the unified pipe yields them in strict source-Sequence order —
        // the property cross-pipe-ordering-sensitive consumers (Pin, Weather,
        // Position) depend on. Discard / Anomaly lines never reach the
        // unified pipe.
        var upstream = new ScriptedPlayerLogStream();
        using var classifier = new PlayerLogClassifier(upstream);

        var collected = new List<LogEnvelope<IClassifiedPlayerLogLine>>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = Task.Run(async () =>
        {
            await foreach (var env in ((IClassifiedPlayerLogStream)classifier)
                .SubscribeWithReplayMarkerAsync(cts.Token))
            {
                collected.Add(env);
                if (collected.Count >= 5) break;
            }
        });

        upstream.Push("[20:01:14] LOADING LEVEL AreaKurMountains");
        upstream.Push("[20:01:17] LocalPlayer: ProcessAddItem(GoblinCap(84741837), -1, False)");
        upstream.Push("UnloadTime: 4.729400 ms"); // discard — not on unified pipe
        upstream.Push("[20:01:19] entity_25021745: OnAttackHitMe(Bear Bite (Pet)). Evaded = False");
        upstream.Push("[20:01:20] LocalPlayer: ProcessRemoveEffects(25042203, [259278,])");
        upstream.Push("[20:01:14] Logged in as character TestChar. Time UTC=05/19/2026 20:01:14. Timezone Offset 01:00:00");

        await task.WaitAsync(TimeSpan.FromSeconds(5));

        collected.Should().HaveCount(5);
        // Strict monotonic Sequence — exactly the invariant the L1 driver
        // relies on for its SkipProcessedHighWater filter.
        collected.Select(e => e.Payload.Sequence).Should().BeInAscendingOrder();
        // Each envelope's runtime type matches the source line kind.
        collected[0].Payload.Should().BeOfType<SystemSignalLogLine>();
        collected[1].Payload.Should().BeOfType<LocalPlayerLogLine>();
        collected[2].Payload.Should().BeOfType<CombatActorLogLine>();
        collected[3].Payload.Should().BeOfType<LocalPlayerLogLine>();
        collected[4].Payload.Should().BeOfType<SystemSignalLogLine>();
    }

    [Fact]
    public async Task IsReplay_flips_exactly_once_at_structural_boundary()
    {
        // Pre-seed three lines, then attach a holder + a late marker
        // subscriber. The marker subscriber should see IsReplay=true for
        // the buffered backlog and IsReplay=false from the first live
        // emission onwards — and the flip must happen exactly once, not
        // flap.
        var upstream = new ScriptedPlayerLogStream();
        using var classifier = new PlayerLogClassifier(upstream);

        // First, attach a holder that keeps the replay buffer populated.
        using var holderCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var holderSink = new List<LogEnvelope<IClassifiedPlayerLogLine>>();
        var holderTask = Task.Run(async () =>
        {
            await foreach (var item in ((IClassifiedPlayerLogStream)classifier)
                .SubscribeWithReplayMarkerAsync(holderCts.Token))
                holderSink.Add(item);
        });

        upstream.Push("[20:01:17] LocalPlayer: ProcessAddItem(A(1), -1, False)");
        upstream.Push("[20:01:18] LocalPlayer: ProcessAddItem(B(2), -1, False)");
        upstream.Push("[20:01:19] LocalPlayer: ProcessAddItem(C(3), -1, False)");

        await WaitUntilAsync(() => holderSink.Count >= 3, TimeSpan.FromSeconds(5));

        // Late marker subscriber: 3 replay + 2 live.
        var collected = new List<LogEnvelope<IClassifiedPlayerLogLine>>();
        using var lateCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lateTask = Task.Run(async () =>
        {
            await foreach (var env in ((IClassifiedPlayerLogStream)classifier)
                .SubscribeWithReplayMarkerAsync(lateCts.Token))
            {
                collected.Add(env);
                if (collected.Count >= 5) break;
            }
        });

        await WaitUntilAsync(() => collected.Count >= 3, TimeSpan.FromSeconds(5));
        upstream.Push("[20:01:20] LocalPlayer: ProcessAddItem(D(4), -1, False)");
        upstream.Push("[20:01:21] LocalPlayer: ProcessAddItem(E(5), -1, False)");
        await lateTask.WaitAsync(TimeSpan.FromSeconds(5));

        collected.Should().HaveCount(5);

        // Exactly the first 3 are IsReplay=true; the remaining 2 are false.
        // The flip happens exactly once — there's no `true → false → true`
        // flapping or `false → true` regression.
        var replayBits = collected.Select(e => e.IsReplay).ToList();
        replayBits.Should().Equal(true, true, true, false, false);

        holderCts.Cancel();
        try { await holderTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Discard_and_anomaly_lines_never_reach_the_unified_pipe()
    {
        var upstream = new ScriptedPlayerLogStream();
        using var classifier = new PlayerLogClassifier(upstream);

        var collected = new List<LogEnvelope<IClassifiedPlayerLogLine>>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var env in ((IClassifiedPlayerLogStream)classifier)
                    .SubscribeWithReplayMarkerAsync(cts.Token))
                    collected.Add(env);
            }
            catch (OperationCanceledException) { }
        });

        // Push only discard + anomaly shapes:
        upstream.Push("UnloadTime: 4.729400 ms");                                 // discard
        upstream.Push("entity_24902175_skin : destroying FluffySheep #-1");       // discard
        upstream.Push("[20:01:17] !!! Initializing area! (502934): AreaKurMt");   // anomaly
        upstream.Push("[20:01:17] New Network State: PickingCharacter -> ...");   // anomaly

        await task;
        collected.Should().BeEmpty();

        var counters = classifier.Counters;
        counters.Discarded.Should().Be(2);
        counters.Anomaly.Should().Be(2);
    }

    [Fact]
    public async Task Counter_snapshot_distinguishes_discard_anomaly_and_sample_budget()
    {
        var upstream = new ScriptedPlayerLogStream();
        using var classifier = new PlayerLogClassifier(upstream);

        var collected = new List<LogEnvelope<IClassifiedPlayerLogLine>>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var task = Task.Run(async () =>
        {
            try
            {
                await foreach (var env in ((IClassifiedPlayerLogStream)classifier)
                    .SubscribeWithReplayMarkerAsync(cts.Token))
                    collected.Add(env);
            }
            catch (OperationCanceledException) { }
        });

        // 1 LocalPlayer + 3 discards + 12 anomalies. The anomaly sample
        // budget caps at 8 — so AnomalySamplesEmitted should plateau at 8
        // even though anomaly count reaches 12.
        upstream.Push("[20:01:17] LocalPlayer: ProcessAddItem(A(1), -1, False)");
        for (int i = 0; i < 3; i++) upstream.Push($"UnloadTime: 0.{i} ms");
        for (int i = 0; i < 12; i++) upstream.Push($"[20:01:1{i % 10}] zz unknown shape {i}");

        await task;

        var counters = classifier.Counters;
        counters.Discarded.Should().Be(3);
        counters.Anomaly.Should().Be(12);
        counters.AnomalySamplesEmitted.Should().BeLessThanOrEqualTo(8);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("WaitUntilAsync gave up");
            await Task.Delay(10);
        }
    }

    private sealed class ScriptedPlayerLogStream : IPlayerLogStream
    {
        private readonly Channel<RawLogLine> _ch = Channel.CreateUnbounded<RawLogLine>();
        private long _seq;

        public void Push(string line)
        {
            var seq = Interlocked.Increment(ref _seq);
            _ch.Writer.TryWrite(new RawLogLine(
                Timestamp: new DateTimeOffset(2026, 5, 19, 20, 0, 0, TimeSpan.Zero),
                Line: line,
                Sequence: seq,
                ReadMonotonicTicks: 0));
        }

        public async IAsyncEnumerable<RawLogLine> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
        {
            while (await _ch.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_ch.Reader.TryRead(out var line))
                    yield return line;
            }
        }
    }
}
