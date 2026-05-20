using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests.Logging;

/// <summary>
/// L0.5 (#532) router end-to-end tests. Drives the classifier via the
/// router with an in-memory <see cref="IPlayerLogStream"/> and asserts the
/// fan-out + the <c>Raw</c> opt-in behaviour.
/// </summary>
public sealed class PlayerLogActorRouterTests
{
    [Fact]
    public async Task Router_fans_out_to_three_typed_pipes()
    {
        var upstream = new ScriptedPlayerLogStream();
        using var router = new PlayerLogActorRouter(upstream);

        var localTask = CollectAsync(router.SubscribeAsync, 3);
        var combatTask = CollectAsync(((ICombatActorLogStream)router).SubscribeAsync, 1);
        var systemTask = CollectAsync(((ISystemSignalLogStream)router).SubscribeAsync, 2);

        // Feed a mixed batch:
        upstream.Push("[20:01:14] LOADING LEVEL AreaKurMountains");
        upstream.Push("[20:01:14] Logged in as character TestChar. Time UTC=05/19/2026 20:01:14. Timezone Offset 01:00:00");
        upstream.Push("[20:01:17] LocalPlayer: ProcessAddItem(GoblinCap(84741837), -1, False)");
        upstream.Push("[20:01:18] LocalPlayer: ProcessSetAttributes(25042203, \"[]\", \"[]\")");
        upstream.Push("[20:01:19] entity_25021745: OnAttackHitMe(Bear Bite (Pet)). Evaded = False");
        upstream.Push("[20:01:20] LocalPlayer: ProcessRemoveEffects(25042203, [259278,])");
        upstream.Push("UnloadTime: 4.729400 ms"); // discard
        upstream.Push("entity_24902175_skin : destroying FluffySheep (Instance) #-153844"); // discard

        var local = await localTask.WaitAsync(TimeSpan.FromSeconds(5));
        var combat = await combatTask.WaitAsync(TimeSpan.FromSeconds(5));
        var system = await systemTask.WaitAsync(TimeSpan.FromSeconds(5));

        local.Should().HaveCount(3);
        local[0].Data.Should().StartWith("ProcessAddItem(");
        local[1].Data.Should().StartWith("ProcessSetAttributes(");
        local[2].Data.Should().StartWith("ProcessRemoveEffects(");

        combat.Should().HaveCount(1);
        combat[0].EntityId.Should().Be(25021745L);
        combat[0].Data.Should().StartWith("OnAttackHitMe(");

        system.Should().HaveCount(2);
        system.Should().ContainSingle(s => s.Kind == SystemSignalKind.AreaLoading);
        system.Should().ContainSingle(s => s.Kind == SystemSignalKind.LoginBanner);

        var counters = router.Counters;
        counters.Discarded.Should().Be(2);
        counters.Anomaly.Should().Be(0);
    }

    [Fact]
    public async Task Raw_is_null_by_default_and_filled_when_accessor_returns_true()
    {
        // Two routers — one with the toggle off (default), one with it on —
        // each fed an identical line; assert the toggle controls Raw.
        var offUpstream = new ScriptedPlayerLogStream();
        using var offRouter = new PlayerLogActorRouter(offUpstream, captureRawAccessor: () => false);
        var offTask = CollectAsync(offRouter.SubscribeAsync, 1);
        offUpstream.Push("[20:01:17] LocalPlayer: ProcessAddItem(GoblinCap(84741837), -1, False)");
        var off = await offTask.WaitAsync(TimeSpan.FromSeconds(5));
        off[0].Raw.Should().BeNull();

        var onUpstream = new ScriptedPlayerLogStream();
        using var onRouter = new PlayerLogActorRouter(onUpstream, captureRawAccessor: () => true);
        var onTask = CollectAsync(onRouter.SubscribeAsync, 1);
        var rawLine = "[20:01:17] LocalPlayer: ProcessAddItem(GoblinCap(84741837), -1, False)";
        onUpstream.Push(rawLine);
        var on = await onTask.WaitAsync(TimeSpan.FromSeconds(5));
        on[0].Raw.Should().Be(rawLine);
    }

    [Fact]
    public async Task Anomaly_lines_are_counted_without_being_emitted_on_any_pipe()
    {
        var upstream = new ScriptedPlayerLogStream();
        using var router = new PlayerLogActorRouter(upstream);

        // Late-subscribe so we have a chance to count anomalies without
        // any pipe receiving them.
        var localTask = CollectAsync(router.SubscribeAsync, 0, TimeSpan.FromMilliseconds(400));
        var combatTask = CollectAsync(((ICombatActorLogStream)router).SubscribeAsync, 0, TimeSpan.FromMilliseconds(400));
        var systemTask = CollectAsync(((ISystemSignalLogStream)router).SubscribeAsync, 0, TimeSpan.FromMilliseconds(400));

        // Genuinely unknown shapes the classifier should escalate to anomaly:
        upstream.Push("[20:01:17] !!! Initializing area! (502934): AreaKurMountains");
        upstream.Push("[20:01:17] New Network State: PickingCharacter -> JoinedArea)");

        var local = await localTask;
        var combat = await combatTask;
        var system = await systemTask;

        local.Should().BeEmpty();
        combat.Should().BeEmpty();
        system.Should().BeEmpty();
        router.Counters.Anomaly.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Late_subscriber_sees_replay_then_live()
    {
        // The replay buffer is alive only while at least one subscriber holds
        // a pipe open — same shape as PlayerLogStream. To exercise the
        // replay path the first subscriber stays attached while the late
        // subscriber joins.
        var upstream = new ScriptedPlayerLogStream();
        using var router = new PlayerLogActorRouter(upstream);
        using var firstCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // First subscriber: kept open for the duration of the test to keep the
        // replay buffer populated. Collects without an item budget; we only
        // care that it stays attached.
        var firstSink = new List<LocalPlayerLogLine>();
        var firstTask = Task.Run(async () =>
        {
            await foreach (var item in router.SubscribeAsync(firstCts.Token))
                firstSink.Add(item);
        });

        upstream.Push("[20:01:17] LocalPlayer: ProcessAddItem(A(1), -1, False)");
        // Wait for the first subscriber to actually observe the line so the
        // replay buffer has had time to accumulate it.
        await WaitUntilAsync(() => firstSink.Count >= 1, TimeSpan.FromSeconds(5));

        // Subscribe AFTER the first line was emitted. The router's per-pipe
        // replay buffer should deliver it to this new subscriber.
        var lateTask = CollectAsync(router.SubscribeAsync, 2);
        upstream.Push("[20:01:18] LocalPlayer: ProcessAddItem(B(2), -1, False)");

        var collected = await lateTask.WaitAsync(TimeSpan.FromSeconds(5));
        collected.Should().HaveCount(2);
        collected[0].Data.Should().StartWith("ProcessAddItem(A");
        collected[1].Data.Should().StartWith("ProcessAddItem(B");

        firstCts.Cancel();
        try { await firstTask; } catch (OperationCanceledException) { }
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

    private static async Task<List<T>> CollectAsync<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> subscribe,
        int expected,
        TimeSpan? timeBudget = null)
    {
        var budget = timeBudget ?? TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(budget);
        var result = new List<T>();
        try
        {
            await foreach (var item in subscribe(cts.Token).ConfigureAwait(false))
            {
                result.Add(item);
                if (result.Count >= expected) break;
            }
        }
        catch (OperationCanceledException) { /* expected when expected=0 */ }
        return result;
    }

    [Fact]
    public async Task SubscribeWithReplayMarker_FlagsReplayThenLiveAuthoritatively()
    {
        // L1 (#550 PR 1) added the *WithReplayMarker variant; the router's
        // direct-yield-replay branch flags IsReplay=true, the channel-read
        // branch flags IsReplay=false. The L1 driver reads this bit; the
        // L0.5 API gains a corresponding pin so future router refactors
        // can't silently drop the boundary signal.
        var upstream = new ScriptedPlayerLogStream();
        using var router = new PlayerLogActorRouter(upstream);

        // Seed three lines into the upstream BEFORE we subscribe, so the
        // router's per-pipe replay buffer accumulates them. Then attach a
        // first subscriber that holds the replay buffer open while a
        // second subscriber late-joins via the marker variant.
        upstream.Push("[20:01:17] LocalPlayer: ProcessAddItem(A(1), -1, False)");
        upstream.Push("[20:01:18] LocalPlayer: ProcessAddItem(B(2), -1, False)");

        using var holderCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var holderSink = new List<LocalPlayerLogLine>();
        var holderTask = Task.Run(async () =>
        {
            await foreach (var item in router.SubscribeAsync(holderCts.Token))
                holderSink.Add(item);
        });
        await WaitUntilAsync(() => holderSink.Count >= 2, TimeSpan.FromSeconds(5));

        // Late-subscribe through the marker variant. The replay buffer
        // should hand both prior lines back as IsReplay=true.
        var collected = new List<LogEnvelope<LocalPlayerLogLine>>();
        using var lateCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lateTask = Task.Run(async () =>
        {
            await foreach (var envelope in router.SubscribeWithReplayMarkerAsync(lateCts.Token))
            {
                collected.Add(envelope);
                if (collected.Count >= 3) break;
            }
        });

        await WaitUntilAsync(() => collected.Count >= 2, TimeSpan.FromSeconds(5));
        // Push one live line — the marker variant should flag it false
        upstream.Push("[20:01:19] LocalPlayer: ProcessAddItem(C(3), -1, False)");
        await lateTask.WaitAsync(TimeSpan.FromSeconds(5));

        collected.Should().HaveCount(3);
        collected[0].IsReplay.Should().BeTrue();
        collected[0].Payload.Data.Should().StartWith("ProcessAddItem(A");
        collected[1].IsReplay.Should().BeTrue();
        collected[1].Payload.Data.Should().StartWith("ProcessAddItem(B");
        collected[2].IsReplay.Should().BeFalse();
        collected[2].Payload.Data.Should().StartWith("ProcessAddItem(C");

        holderCts.Cancel();
        try { await holderTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Fast_unsub_then_resub_does_not_double_route_lines_to_new_subscriber()
    {
        // #547 race: PlayerLogActorRouter.StopRunning previously nulled _runTask
        // synchronously while the prior RunAsync's `await foreach` was still
        // unwinding. A fast unsubscribe → subscribe → push then spawned a
        // SECOND RunAsync alongside the still-draining first; both routed the
        // same upstream emission to the new subscriber's pipe channel.
        //
        // We expose the window deterministically with a per-subscriber upstream
        // whose enumerator only completes once externally released — the
        // router's old RunAsync remains alive and subscribed to upstream
        // through the moment the new subscriber attaches.
        var upstream = new BlockingPerSubscriberStream();
        using var router = new PlayerLogActorRouter(upstream);

        // First subscriber: receives line 1 then closes.
        var firstCollected = await CollectOneThenCloseAsync(router.SubscribeAsync, async () =>
        {
            await WaitUntilAsync(() => upstream.SubscriberCount == 1, TimeSpan.FromSeconds(5));
            upstream.Push("[20:01:17] LocalPlayer: ProcessAddItem(A(1), -1, False)");
        });
        firstCollected.Should().HaveCount(1);
        firstCollected[0].Data.Should().StartWith("ProcessAddItem(A");

        // At this moment: the first consumer has exited (StopRunning fired
        // synchronously inside its finally{}), but the prior RunAsync may
        // still be inside upstream's await foreach because the upstream
        // enumerator hasn't been released yet — exactly the race window.

        // Second subscriber attaches. With the bug, EnsureRunning saw
        // _runTask is null (StopRunning had nulled it) and spawned a SECOND
        // RunAsync alongside the still-draining first; upstream then had
        // TWO subscribers and any push routed twice to the new pipe channel.
        //
        // With the chain-serialize fix, EnsureRunning queues the new run via
        // ContinueWith — it only subscribes to upstream after the prior run
        // unwinds. So while upstream is held open, only the PRIOR RunAsync
        // is subscribed; the new pipe channel receives exactly one
        // emission per push (routed by the prior RunAsync, which is still
        // alive and observing PipeRegistry).
        //
        // CollectAsync runs synchronously up to the first await in its body
        // — past `router.SubscribeAsync.GetAsyncEnumerator().MoveNextAsync()`,
        // which has already done lock + AddSubscriber + EnsureRunning by the
        // time `lateTask` is assigned, so the new pipe subscriber is in
        // place. We still wait briefly for upstream to reach two subscribers
        // — on UNFIXED code this succeeds (proves the race is observably
        // open) and a subsequent push routes twice; on FIXED code this
        // times out (only one subscriber ever; the chain serializes
        // restarts), the catch swallows the timeout, and the subsequent
        // push routes only once via the still-alive prior RunAsync. Either
        // branch leads to the same assertion: exactly one emission.
        var lateTask = CollectAsync(router.SubscribeAsync, expected: 5, timeBudget: TimeSpan.FromSeconds(2));
        try { await WaitUntilAsync(() => upstream.SubscriberCount >= 2, TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { /* fix path — count stays at 1; proceed */ }

        upstream.Push("[20:01:18] LocalPlayer: ProcessAddItem(B(2), -1, False)");
        var collected = await lateTask;

        // Release the held-open upstream subscriptions so they unwind cleanly
        // for the test's disposal.
        upstream.Release();

        // Exactly-once: the new subscriber observes line B once, not twice.
        // Before the fix: 2 items (both RunAsync instances routed the line).
        // After the fix:  1 item (only one RunAsync subscribed at a time).
        collected.Should().HaveCount(1,
            because: $"observed Data values: [{string.Join(", ", collected.Select(x => $"\"{x.Data[..Math.Min(30, x.Data.Length)]}\""))}]");
        collected[0].Data.Should().StartWith("ProcessAddItem(B");
    }

    /// <summary>
    /// Subscribe once, run <paramref name="trigger"/> while subscribed, collect
    /// exactly one item, then exit the iteration (triggering the router's
    /// no-subscribers StopRunning path).
    /// </summary>
    private static async Task<List<LocalPlayerLogLine>> CollectOneThenCloseAsync(
        Func<CancellationToken, IAsyncEnumerable<LocalPlayerLogLine>> subscribe,
        Func<Task> trigger)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = new List<LocalPlayerLogLine>();
        var triggerTask = Task.Run(trigger);
        await foreach (var item in subscribe(cts.Token))
        {
            result.Add(item);
            if (result.Count >= 1) break;
        }
        await triggerTask;
        return result;
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

    /// <summary>
    /// Per-subscriber upstream that mirrors <see cref="PlayerLogStream"/>'s
    /// real fan-out shape (each subscriber gets its own channel — needed to
    /// reproduce #547; a shared-channel test stream hides the bug because
    /// only one router instance ever reads the line). Subscriber enumerators
    /// stay alive after cancellation until <see cref="Release"/> is called,
    /// so a test can deterministically hold open the prior RunAsync's
    /// `await foreach` while a new subscriber attaches.
    /// </summary>
    private sealed class BlockingPerSubscriberStream : IPlayerLogStream
    {
        private readonly object _gate = new();
        private readonly List<Channel<RawLogLine>> _subs = new();
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _seq;

        public int SubscriberCount { get { lock (_gate) return _subs.Count; } }

        public void Push(string line)
        {
            Channel<RawLogLine>[] snapshot;
            lock (_gate) snapshot = _subs.ToArray();
            var seq = Interlocked.Increment(ref _seq);
            var raw = new RawLogLine(
                Timestamp: new DateTimeOffset(2026, 5, 19, 20, 0, 0, TimeSpan.Zero),
                Line: line, Sequence: seq, ReadMonotonicTicks: 0);
            foreach (var ch in snapshot) ch.Writer.TryWrite(raw);
        }

        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<RawLogLine> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var ch = Channel.CreateUnbounded<RawLogLine>();
            lock (_gate) _subs.Add(ch);
            try
            {
                // Pump lines as they arrive. We deliberately do NOT pass `ct`
                // to WaitToReadAsync so a cancellation does not immediately
                // exit the iteration — the enumerator only completes once
                // `Release()` fires (or the channel is closed). This matches
                // the spirit of an upstream whose `await foreach` is "still
                // in flight" while the router's StopRunning has already
                // returned.
                while (!_release.Task.IsCompleted)
                {
                    var any = await Task.WhenAny(
                        ch.Reader.WaitToReadAsync().AsTask(),
                        _release.Task);
                    if (any == _release.Task) break;
                    while (ch.Reader.TryRead(out var line)) yield return line;
                }
            }
            finally
            {
                lock (_gate) _subs.Remove(ch);
            }
        }
    }
}
