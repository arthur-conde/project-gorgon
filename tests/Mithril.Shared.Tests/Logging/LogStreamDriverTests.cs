using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows.Threading;
using FluentAssertions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests.Logging;

/// <summary>
/// L1 driver tests (#511 deliverable 3 / #550 PR 1). Covers capabilities
/// A-G end-to-end against in-memory upstream stubs that reproduce L0/L0.5's
/// "direct-yield replay buffer → bounded-channel live tail" structural shape.
/// The driver's IsReplay determination relies on this shape; the stubs
/// preserve it.
/// </summary>
public sealed class LogStreamDriverTests
{
    // ============================================================
    // Capability A + B — ReplayMode and IsReplay
    // ============================================================

    [Fact]
    public async Task IsReplay_FlipsAtFirstAsyncMoveNext()
    {
        // Capability B — the IsReplay bit must be true exactly during the
        // replay drain and false from the first live envelope onward. The
        // driver detects the boundary via MoveNextAsync.IsCompletedSuccessfully:
        // the upstream's direct-yield replay branch completes synchronously,
        // the bounded-channel live branch awaits.
        var upstream = new TwoPhaseLocalPlayerStream();
        upstream.AddReplay(Make("ProcessAddItem(A,1)", seq: 1));
        upstream.AddReplay(Make("ProcessAddItem(B,1)", seq: 2));
        upstream.AddReplay(Make("ProcessAddItem(C,1)", seq: 3));

        using var driver = NewDriverWith(localPlayer: upstream);
        var collected = new List<LogEnvelope<LocalPlayerLogLine>>();
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e => { lock (collected) collected.Add(e); return ValueTask.CompletedTask; });

        await WaitUntilAsync(() => { lock (collected) return collected.Count == 3; });

        // Push a live line — only emitted after the replay buffer drains
        upstream.PushLive(Make("ProcessAddItem(LIVE,1)", seq: 4));
        await WaitUntilAsync(() => { lock (collected) return collected.Count == 4; });

        lock (collected)
        {
            collected.Should().HaveCount(4);
            collected[0].IsReplay.Should().BeTrue("replay phase");
            collected[1].IsReplay.Should().BeTrue();
            collected[2].IsReplay.Should().BeTrue();
            collected[3].IsReplay.Should().BeFalse("first live envelope after the channel await");
            collected[3].Payload.Data.Should().Contain("LIVE");
        }
    }

    [Fact]
    public async Task ReplayMode_LiveOnly_DropsReplayPhaseEntirely()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        upstream.AddReplay(Make("REPLAY1", seq: 1));
        upstream.AddReplay(Make("REPLAY2", seq: 2));

        using var driver = NewDriverWith(localPlayer: upstream);
        var collected = new List<LogEnvelope<LocalPlayerLogLine>>();
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e => { lock (collected) collected.Add(e); return ValueTask.CompletedTask; },
            new LogSubscriptionOptions { ReplayMode = ReplayMode.LiveOnly });

        // Give the pump a chance to drain replay (which should be dropped)
        upstream.PushLive(Make("LIVE1", seq: 3));
        await WaitUntilAsync(() => { lock (collected) return collected.Count >= 1; });

        // The replay items must NOT have been delivered
        lock (collected)
        {
            collected.Should().HaveCount(1);
            collected[0].Payload.Data.Should().Be("LIVE1");
            collected[0].IsReplay.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ReplayMode_SinceSubscribe_DropsReplayPhaseEntirely()
    {
        // SinceSubscribe is documented (ReplayMode.cs) as behaviourally
        // identical to LiveOnly today. The driver MUST honour that —
        // Saruman/Discovery's #549 disposition picks SinceSubscribe
        // explicitly to express intent, and its eventual migration would
        // silently re-inflate DiscoveryCount if replay leaked through.
        var upstream = new TwoPhaseLocalPlayerStream();
        upstream.AddReplay(Make("REPLAY1", seq: 1));
        upstream.AddReplay(Make("REPLAY2", seq: 2));

        using var driver = NewDriverWith(localPlayer: upstream);
        var collected = new List<LogEnvelope<LocalPlayerLogLine>>();
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e => { lock (collected) collected.Add(e); return ValueTask.CompletedTask; },
            new LogSubscriptionOptions { ReplayMode = ReplayMode.SinceSubscribe });

        // Give the pump a chance to drain replay (which should be dropped)
        upstream.PushLive(Make("LIVE1", seq: 3));
        await WaitUntilAsync(() => { lock (collected) return collected.Count >= 1; });

        // The replay items must NOT have been delivered
        lock (collected)
        {
            collected.Should().HaveCount(1);
            collected[0].Payload.Data.Should().Be("LIVE1");
            collected[0].IsReplay.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ReplayMode_FromSessionStart_DeliversBacklogThenLive()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        upstream.AddReplay(Make("R1", seq: 1));
        upstream.AddReplay(Make("R2", seq: 2));

        using var driver = NewDriverWith(localPlayer: upstream);
        var collected = new List<LogEnvelope<LocalPlayerLogLine>>();
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e => { lock (collected) collected.Add(e); return ValueTask.CompletedTask; },
            new LogSubscriptionOptions { ReplayMode = ReplayMode.FromSessionStart });

        await WaitUntilAsync(() => { lock (collected) return collected.Count == 2; });

        upstream.PushLive(Make("L1", seq: 3));
        await WaitUntilAsync(() => { lock (collected) return collected.Count == 3; });

        lock (collected)
        {
            collected.Select(e => e.Payload.Data).Should().Equal("R1", "R2", "L1");
            collected[0].IsReplay.Should().BeTrue();
            collected[1].IsReplay.Should().BeTrue();
            collected[2].IsReplay.Should().BeFalse();
        }
    }

    // ============================================================
    // Capability C — Per-message error containment
    // ============================================================

    [Fact]
    public async Task HandlerThrow_IsCaught_AndDoesNotKillThePump()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        using var diag = new CapturingDiag();
        using var driver = NewDriverWith(localPlayer: upstream, diag: diag);

        var seenAfterThrow = 0;
        using var sub = driver.Subscribe<LocalPlayerLogLine>(e =>
        {
            if (e.Payload.Data == "BOOM") throw new InvalidOperationException("test boom");
            Interlocked.Increment(ref seenAfterThrow);
            return ValueTask.CompletedTask;
        });

        upstream.PushLive(Make("BOOM", seq: 1));
        upstream.PushLive(Make("OK1", seq: 2));
        upstream.PushLive(Make("OK2", seq: 3));

        await WaitUntilAsync(() => Volatile.Read(ref seenAfterThrow) >= 2);

        sub.Diagnostics.HandlerFailures.Should().Be(1);
        sub.Diagnostics.Delivered.Should().Be(2);
        diag.WarnCount.Should().BeGreaterThan(0,
            because: "ThrottledWarn should have surfaced the failure");
    }

    [Fact]
    public async Task HandlerThrow_Warn_IsRateLimited()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        using var diag = new CapturingDiag();
        using var driver = NewDriverWith(localPlayer: upstream, diag: diag);

        using var sub = driver.Subscribe<LocalPlayerLogLine>(e =>
            throw new InvalidOperationException("always boom"));

        for (var i = 0; i < 50; i++) upstream.PushLive(Make($"L{i}", seq: i + 1));
        await WaitUntilAsync(() => sub.Diagnostics.HandlerFailures >= 50);

        // 50 failures must not produce 50 Warns — ThrottledWarn caps emission
        diag.WarnCount.Should().BeLessThan(10,
            because: "ThrottledWarn defaults to ~one warn per 5 seconds; 50 failures in <1s should produce <10 emitted warns");
    }

    // ============================================================
    // Capability D — Drop accounting
    // ============================================================

    [Fact]
    public async Task DropCounter_IncrementsWhenMarshaledBridgeBacklogFills()
    {
        // The Marshaled bridge bounds its queue at 1024. A handler that
        // blocks the dispatcher must surface drops once we push >1024
        // items at it while the dispatcher is stalled.
        using var dispatcher = StaThread.Start();
        var upstream = new TwoPhaseLocalPlayerStream();
        using var driver = NewDriverWith(localPlayer: upstream);

        var release = new TaskCompletionSource();
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            async e => { await release.Task.ConfigureAwait(true); },
            new LogSubscriptionOptions
            {
                DeliveryContext = DeliveryContext.Marshaled(dispatcher.Dispatcher),
            });

        // Fire enough to overflow the bridge queue (1024 capacity).
        const int totalToPush = 1500;
        for (var i = 0; i < totalToPush; i++)
        {
            upstream.PushLive(Make($"L{i}", seq: i + 1));
        }

        // The dispatcher is stalled on `release`. Wait for the pump to
        // have observed > capacity envelopes and accounted overflows.
        await WaitUntilAsync(() => sub.Diagnostics.Dropped > 0, TimeSpan.FromSeconds(10));
        sub.Diagnostics.Dropped.Should().BeGreaterThan(0);

        // Release the dispatcher so cleanup proceeds quickly
        release.TrySetResult();
        dispatcher.Shutdown();
    }

    // ============================================================
    // Capability E — DeliveryContext (Marshaled vs Inline)
    // ============================================================

    [Fact]
    public async Task DeliveryContext_Marshaled_RunsHandlerOnDispatcherThread()
    {
        using var dispatcher = StaThread.Start();
        var upstream = new TwoPhaseLocalPlayerStream();
        using var driver = NewDriverWith(localPlayer: upstream);

        var observedThreadIds = new List<int>();
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e =>
            {
                lock (observedThreadIds) observedThreadIds.Add(Environment.CurrentManagedThreadId);
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions { DeliveryContext = DeliveryContext.Marshaled(dispatcher.Dispatcher) });

        upstream.PushLive(Make("X", seq: 1));
        upstream.PushLive(Make("Y", seq: 2));
        await WaitUntilAsync(() => sub.Diagnostics.Delivered >= 2);

        lock (observedThreadIds)
        {
            observedThreadIds.Should().HaveCount(2);
            observedThreadIds[0].Should().Be(dispatcher.ThreadId);
            observedThreadIds[1].Should().Be(dispatcher.ThreadId);
            observedThreadIds[0].Should().NotBe(Environment.CurrentManagedThreadId,
                because: "the test thread is NOT the dispatcher thread");
        }
        dispatcher.Shutdown();
    }

    [Fact]
    public async Task DeliveryContext_Marshaled_AsyncHandler_AwaitsInnerTask_AndCapturesPostAwaitExceptions()
    {
        // Regression — without `.Task.Unwrap()` in MarshaledBridge.DrainAsync,
        // Dispatcher.InvokeAsync(async () => ...) returns a Task<Task>; the
        // outer task completes when the dispatcher *starts* the lambda,
        // making the inner async body fire-and-forget. That breaks two
        // promises:
        //   (1) sequential delivery — concurrent envelopes can interleave;
        //   (2) post-await exception capture — exceptions thrown after the
        //       handler's first await escape the bridge's try/catch and are
        //       lost.
        using var dispatcher = StaThread.Start();
        var upstream = new TwoPhaseLocalPlayerStream();
        using var driver = NewDriverWith(localPlayer: upstream);

        var inFlight = 0;
        var maxConcurrent = 0;
        var order = new List<string>();

        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            async e =>
            {
                var now = Interlocked.Increment(ref inFlight);
                // Track the peak in-flight count atomically so a CAS race
                // between two concurrent invocations can't lose an update.
                int observed;
                do
                {
                    observed = Volatile.Read(ref maxConcurrent);
                    if (now <= observed) break;
                }
                while (Interlocked.CompareExchange(ref maxConcurrent, now, observed) != observed);

                await Task.Delay(50).ConfigureAwait(true);
                // Post-await — if the bridge does not Unwrap, the throw
                // here is lost and HandlerFailures stays at 0.
                if (e.Payload.Data == "THROW_AFTER_AWAIT")
                {
                    Interlocked.Decrement(ref inFlight);
                    throw new InvalidOperationException("post-await throw");
                }
                lock (order) order.Add(e.Payload.Data);
                Interlocked.Decrement(ref inFlight);
            },
            new LogSubscriptionOptions { DeliveryContext = DeliveryContext.Marshaled(dispatcher.Dispatcher) });

        upstream.PushLive(Make("A", seq: 1));
        upstream.PushLive(Make("B", seq: 2));
        upstream.PushLive(Make("THROW_AFTER_AWAIT", seq: 3));
        upstream.PushLive(Make("C", seq: 4));

        await WaitUntilAsync(
            () => sub.Diagnostics.Delivered + sub.Diagnostics.HandlerFailures >= 4,
            TimeSpan.FromSeconds(10));

        // (1) Sequential delivery — peak in-flight is 1, never 2+.
        Volatile.Read(ref maxConcurrent).Should().Be(1,
            because: "the drain awaits the inner async task via Unwrap; handlers must not run concurrently");

        // (2) Post-await exception captured — HandlerFailures incremented.
        sub.Diagnostics.HandlerFailures.Should().Be(1,
            because: "the post-await throw must surface on HandlerFailures (Critical #1)");

        // (3) Three successful deliveries (A, B, C); order preserved.
        sub.Diagnostics.Delivered.Should().Be(3);
        lock (order) order.Should().Equal("A", "B", "C");

        dispatcher.Shutdown();
    }

    [Fact]
    public async Task MarshaledBridge_DispatcherShutdown_StopsSilentDropSpiral()
    {
        // Important #3 — if DrainAsync exits unexpectedly (e.g. dispatcher
        // shuts down so InvokeAsync.Task.Unwrap() faults), the channel
        // writer must be completed so subsequent DeliverAsync TryWrites
        // no-op rather than spiralling drop accounting forever. The
        // subscription should surface the bridge death on the fault SM
        // (Degraded) rather than staying Healthy while silently dropping
        // every envelope.
        using var dispatcher = StaThread.Start();
        var upstream = new TwoPhaseLocalPlayerStream();
        using var diag = new CapturingDiag();
        var attention = new LogStreamAttentionSource();
        using var driver = new LogStreamDriver(
            upstream, NoopCombat.Instance, NoopSystem.Instance,
            NoopClassified.Instance, NoopChat.Instance,
            attention, diag);

        // Subscribe FIRST so the drain task is running and blocked on
        // ReadAllAsync (channel empty). Then shut down the dispatcher.
        // The next pushed envelope reaches the drain; InvokeAsync on a
        // shut-down dispatcher returns a faulted DispatcherOperation
        // whose .Task.Unwrap() throws TaskCanceledException — exactly
        // the path Important #3 covers.
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e => ValueTask.CompletedTask,
            new LogSubscriptionOptions
            {
                DeliveryContext = DeliveryContext.Marshaled(dispatcher.Dispatcher),
                DegradedAfterConsecutiveFailures = 1,
            });

        // Shut down the dispatcher and wait for it to finish so the next
        // InvokeAsync is GUARANTEED to fault.
        dispatcher.Shutdown();

        // Push the first envelope post-shutdown — the drain pulls it,
        // tries InvokeAsync on the dead dispatcher, faults out.
        upstream.PushLive(Make("L1", seq: 1));

        await WaitUntilAsync(
            () => sub.Diagnostics.State == LogSubscriptionState.Degraded,
            TimeSpan.FromSeconds(5));

        sub.Diagnostics.State.Should().Be(LogSubscriptionState.Degraded,
            because: "a faulted drain must surface on the fault SM, not stay Healthy");

        // After the drain has died, push a wave of envelopes and verify
        // the drop count STABILIZES — i.e. once the channel writer is
        // completed, TryWrite is a no-op and Reader.Count stops being
        // probed against an ever-filling buffer.
        for (var i = 0; i < 2000; i++) upstream.PushLive(Make($"X{i}", seq: i + 100));
        await Task.Delay(200); // let the pump drain the upstream into the (dead) bridge
        var snapshotMid = sub.Diagnostics.Dropped;
        for (var i = 0; i < 2000; i++) upstream.PushLive(Make($"Y{i}", seq: i + 5000));
        await Task.Delay(200);
        var snapshotAfter = sub.Diagnostics.Dropped;

        // Once the writer is completed, TryWrite returns false silently —
        // our probe `preCount >= QueueCapacity` will only fire if the
        // reader count is at-or-above capacity, which is impossible after
        // the channel completes and the buffered items drain. So the drop
        // counter must stabilize (no spiral).
        (snapshotAfter - snapshotMid).Should().BeLessThan(2000,
            because: "after the bridge dies the channel writer is completed; subsequent TryWrites no-op and drops must stabilize, not climb 1-for-1 with pushes");
    }

    [Fact]
    public async Task DeliveryContext_Marshaled_StructurallyPreventsCrossThreadObservableCollectionMutation()
    {
        // The regression test #550 calls out by name: an
        // ObservableCollection<T> bound to the UI thread will throw
        // NotSupportedException when mutated from a non-dispatcher thread
        // (the "This type of CollectionView does not support changes to
        // its SourceCollection from a thread different from the
        // Dispatcher thread" message). Under L1's Marshaled bridge, the
        // mutation runs on the dispatcher thread by construction, so the
        // exception is structurally unreachable.
        using var dispatcher = StaThread.Start();
        var upstream = new TwoPhaseLocalPlayerStream();
        using var driver = NewDriverWith(localPlayer: upstream);

        var collection = await dispatcher.InvokeAsync(() =>
            new System.Collections.ObjectModel.ObservableCollection<string>());

        // Attach a CollectionView to make the cross-thread guard active —
        // a bare ObservableCollection has no thread-affinity check by
        // itself; the guard is on the bound view.
        await dispatcher.InvokeAsync(() =>
        {
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(collection);
            _ = view; // anchor the view so the runtime registers the thread-affinity check
        });

        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e =>
            {
                // Mutate the bound collection. Under Inline this throws
                // (off-thread); under Marshaled it succeeds because the
                // handler runs on the dispatcher.
                collection.Add(e.Payload.Data);
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions { DeliveryContext = DeliveryContext.Marshaled(dispatcher.Dispatcher) });

        upstream.PushLive(Make("M1", seq: 1));
        upstream.PushLive(Make("M2", seq: 2));
        await WaitUntilAsync(() => sub.Diagnostics.Delivered >= 2);

        sub.Diagnostics.HandlerFailures.Should().Be(0,
            because: "Marshaled bridge runs the handler on the dispatcher; cross-thread mutation is structurally impossible");
        var snapshot = await dispatcher.InvokeAsync(() => collection.ToList());
        snapshot.Should().Equal("M1", "M2");

        dispatcher.Shutdown();
    }

    [Fact]
    public async Task DeliveryContext_Inline_RunsHandlerSynchronouslyOnPumpThread()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        using var driver = NewDriverWith(localPlayer: upstream);

        var observedThread = 0;
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e =>
            {
                Interlocked.Exchange(ref observedThread, Environment.CurrentManagedThreadId);
                return ValueTask.CompletedTask;
            });

        upstream.PushLive(Make("X", seq: 1));
        await WaitUntilAsync(() => sub.Diagnostics.Delivered >= 1);

        Volatile.Read(ref observedThread).Should().NotBe(Environment.CurrentManagedThreadId,
            because: "Inline runs on the pump task, not the test thread");
    }

    // ============================================================
    // Capability F — SkipProcessedHighWater
    // ============================================================

    [Fact]
    public async Task SkipProcessedHighWater_DropsEnvelopesAtOrBelowHighWater()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        upstream.AddReplay(Make("R1", seq: 5));
        upstream.AddReplay(Make("R2", seq: 10));
        upstream.AddReplay(Make("R3", seq: 15));
        using var driver = NewDriverWith(localPlayer: upstream);

        var collected = new List<LogEnvelope<LocalPlayerLogLine>>();
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e => { lock (collected) collected.Add(e); return ValueTask.CompletedTask; },
            new LogSubscriptionOptions { SkipProcessedHighWater = 10 });

        upstream.PushLive(Make("L1", seq: 20));
        await WaitUntilAsync(() => { lock (collected) return collected.Count >= 2; });

        lock (collected)
        {
            collected.Select(e => e.Payload.Data).Should().Equal("R3", "L1");
        }
        sub.Diagnostics.HighWaterSkipped.Should().Be(2,
            because: "R1 (seq=5) and R2 (seq=10) are <= highWater=10");
    }

    // ============================================================
    // Capability G — Fault state machine
    // ============================================================

    [Fact]
    public async Task FaultSM_DegradesAfterNConsecutiveFailures_AndRegistersOnAttention()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        using var diag = new CapturingDiag();
        var attention = new LogStreamAttentionSource();
        using var driver = new LogStreamDriver(
            upstream, NoopCombat.Instance, NoopSystem.Instance,
            NoopClassified.Instance, NoopChat.Instance,
            attention, diag);

        var changedFired = 0;
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e => throw new InvalidOperationException("always"),
            new LogSubscriptionOptions { DegradedAfterConsecutiveFailures = 3 });
        sub.StateChanged += (_, _) => Interlocked.Increment(ref changedFired);

        attention.Count.Should().Be(0, "no failures yet");

        for (var i = 0; i < 3; i++) upstream.PushLive(Make($"L{i}", seq: i + 1));
        await WaitUntilAsync(() => sub.Diagnostics.State == LogSubscriptionState.Degraded);

        sub.Diagnostics.State.Should().Be(LogSubscriptionState.Degraded);
        sub.Diagnostics.ConsecutiveFailures.Should().BeGreaterOrEqualTo(3);
        attention.Count.Should().Be(1, "the degraded subscription registers on attention");
        Volatile.Read(ref changedFired).Should().BeGreaterThan(0);
        diag.ErrorCount.Should().Be(1,
            because: "exactly one non-throttled Error is emitted on degraded entry");
    }

    [Fact]
    public async Task FaultSM_RecoversOnNextSuccess_AndDeregistersAttention()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        using var diag = new CapturingDiag();
        var attention = new LogStreamAttentionSource();
        using var driver = new LogStreamDriver(
            upstream, NoopCombat.Instance, NoopSystem.Instance,
            NoopClassified.Instance, NoopChat.Instance,
            attention, diag);

        var failing = true;
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e =>
            {
                if (Volatile.Read(ref failing)) throw new InvalidOperationException("nope");
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions { DegradedAfterConsecutiveFailures = 2 });

        for (var i = 0; i < 2; i++) upstream.PushLive(Make($"B{i}", seq: i + 1));
        await WaitUntilAsync(() => sub.Diagnostics.State == LogSubscriptionState.Degraded);
        attention.Count.Should().Be(1);

        // Flip to success — next delivery should resolve the state
        Volatile.Write(ref failing, false);
        upstream.PushLive(Make("OK", seq: 100));
        await WaitUntilAsync(() => sub.Diagnostics.State == LogSubscriptionState.Healthy);

        sub.Diagnostics.State.Should().Be(LogSubscriptionState.Healthy);
        sub.Diagnostics.ConsecutiveFailures.Should().Be(0);
        attention.Count.Should().Be(0, "successful delivery resolves the attention entry");
    }

    [Fact]
    public async Task FaultSM_KeepsDeliveringEvenWhileDegraded()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        using var driver = NewDriverWith(localPlayer: upstream);

        var seen = 0;
        var alwaysFail = true;
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e =>
            {
                Interlocked.Increment(ref seen);
                if (Volatile.Read(ref alwaysFail)) throw new InvalidOperationException("still bad");
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions { DegradedAfterConsecutiveFailures = 2 });

        for (var i = 0; i < 10; i++) upstream.PushLive(Make($"L{i}", seq: i + 1));
        await WaitUntilAsync(() => Volatile.Read(ref seen) >= 10);

        // Driver kept invoking the handler even after degrading
        Volatile.Read(ref seen).Should().BeGreaterOrEqualTo(10);
        sub.Diagnostics.HandlerFailures.Should().BeGreaterOrEqualTo(10);
        sub.Diagnostics.State.Should().Be(LogSubscriptionState.Degraded);
    }

    // ============================================================
    // Chat behaviour — Divergence 1
    // ============================================================

    [Fact]
    public async Task ChatSubscription_CoercesFromSessionStartToLiveOnly_AndLogsOnce()
    {
        var chat = new ScriptedChatStream();
        using var diag = new CapturingDiag();
        var attention = new LogStreamAttentionSource();
        using var driver = new LogStreamDriver(
            NoopLocalPlayer.Instance, NoopCombat.Instance, NoopSystem.Instance,
            NoopClassified.Instance, chat, attention, diag);

        var collected = new List<LogEnvelope<RawLogLine>>();
        using var sub = driver.Subscribe<RawLogLine>(
            e => { lock (collected) collected.Add(e); return ValueTask.CompletedTask; },
            new LogSubscriptionOptions { ReplayMode = ReplayMode.FromSessionStart });

        chat.Push("hello chat");
        await WaitUntilAsync(() => { lock (collected) return collected.Count >= 1; });

        lock (collected)
        {
            collected.Should().ContainSingle();
            collected[0].IsReplay.Should().BeFalse(
                because: "chat is structurally live-only; replay phase is empty");
        }
        diag.InfoCount.Should().BeGreaterThan(0,
            because: "coercing FromSessionStart→LiveOnly on chat emits a one-time Info diagnostic");
    }

    // ============================================================
    // Mixed-handler subscription — Divergence 2
    // ============================================================

    [Fact]
    public async Task MixedHandler_PerHandlerFilteringOnIsReplay_PreservesSingleSubscription()
    {
        // Divergence 2 (Legolas-style): a single subscription serves both
        // a replay-needing area-bridge handler and a live-only survey
        // dispatch. The driver delivers both phases; the consumer
        // dispatches per-handler using the IsReplay flag.
        var upstream = new TwoPhaseLocalPlayerStream();
        upstream.AddReplay(Make("R1", seq: 1));
        upstream.AddReplay(Make("R2", seq: 2));
        using var driver = NewDriverWith(localPlayer: upstream);

        var areaSeen = new List<string>();
        var liveSeen = new List<string>();

        using var sub = driver.Subscribe<LocalPlayerLogLine>(e =>
        {
            // Area bridge: takes replay
            lock (areaSeen) areaSeen.Add(e.Payload.Data);
            // Survey dispatch: drops replay
            if (!e.IsReplay)
            {
                lock (liveSeen) liveSeen.Add(e.Payload.Data);
            }
            return ValueTask.CompletedTask;
        });

        await WaitUntilAsync(() => { lock (areaSeen) return areaSeen.Count >= 2; });
        upstream.PushLive(Make("L1", seq: 3));
        await WaitUntilAsync(() => { lock (liveSeen) return liveSeen.Count >= 1; });

        lock (areaSeen) areaSeen.Should().Equal("R1", "R2", "L1");
        lock (liveSeen) liveSeen.Should().Equal("L1");
    }

    // ============================================================
    // Type-safety / unsupported types
    // ============================================================

    [Fact]
    public void Subscribe_OfUnsupportedType_Throws()
    {
        using var driver = NewDriverWith();
        Action act = () => driver.Subscribe<string>(_ => ValueTask.CompletedTask);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Subscribe_OfConcreteLocalPlayer_RoutesToTypedPipe_NotUnifiedPipe()
    {
        // Risk 3 pin (#556). The dispatch chain uses `typeof(T) == typeof(...)`
        // exact-equality, so even though LocalPlayerLogLine implements the
        // IClassifiedPlayerLogLine interface, Subscribe<LocalPlayerLogLine>
        // MUST continue to route to the typed pipe (the splitter), NOT the
        // unified pipe (the classifier). A future "simplification" to
        // runtime-type checks would silently break this contract — this
        // test pins it.
        //
        // We assert by direction-of-flow: only the typed-pipe upstream
        // receives any subscribe call; the unified-pipe upstream receives
        // none.
        var typedUpstream = new TwoPhaseLocalPlayerStream();
        var classified = new SubscribeCountingClassifiedStream();
        using var driver = NewDriverWith(localPlayer: typedUpstream, classified: classified);

        var collected = new List<LogEnvelope<LocalPlayerLogLine>>();
        using var sub = driver.Subscribe<LocalPlayerLogLine>(
            e => { lock (collected) collected.Add(e); return ValueTask.CompletedTask; });

        typedUpstream.PushLive(Make("CONCRETE", seq: 1));
        await WaitUntilAsync(() => { lock (collected) return collected.Count >= 1; });

        classified.SubscribeCount.Should().Be(0,
            "Subscribe<LocalPlayerLogLine> must route to the typed pipe (splitter), " +
            "not the unified pipe (classifier), even though LocalPlayerLogLine " +
            "implements IClassifiedPlayerLogLine");
    }

    [Fact]
    public async Task Subscribe_OfClassifiedInterface_RoutesToUnifiedPipe()
    {
        // The companion direction: subscribing to the interface DOES go to
        // the unified pipe.
        var typedUpstream = new TwoPhaseLocalPlayerStream();
        var classified = new SubscribeCountingClassifiedStream();
        using var driver = NewDriverWith(localPlayer: typedUpstream, classified: classified);

        var collected = new List<LogEnvelope<IClassifiedPlayerLogLine>>();
        using var sub = driver.Subscribe<IClassifiedPlayerLogLine>(
            e => { lock (collected) collected.Add(e); return ValueTask.CompletedTask; });

        classified.PushLive(Make("UNIFIED", seq: 1));
        await WaitUntilAsync(() => { lock (collected) return collected.Count >= 1; });

        classified.SubscribeCount.Should().Be(1);
        lock (collected)
        {
            collected.Should().HaveCount(1);
            ((LocalPlayerLogLine)collected[0].Payload).Data.Should().Be("UNIFIED");
        }
    }

    [Fact]
    public void Dispose_OfDriver_DisposesAllSubscriptions()
    {
        var upstream = new TwoPhaseLocalPlayerStream();
        var driver = NewDriverWith(localPlayer: upstream);
        var sub1 = driver.Subscribe<LocalPlayerLogLine>(_ => ValueTask.CompletedTask);
        var sub2 = driver.Subscribe<LocalPlayerLogLine>(_ => ValueTask.CompletedTask);

        driver.Dispose();
        // Calling Dispose on the (already-disposed-by-driver) subscriptions
        // must be safe
        sub1.Dispose();
        sub2.Dispose();

        Action act = () => driver.Subscribe<LocalPlayerLogLine>(_ => ValueTask.CompletedTask);
        act.Should().Throw<ObjectDisposedException>();
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static LocalPlayerLogLine Make(string data, long seq) =>
        new(
            Timestamp: new DateTimeOffset(2026, 5, 19, 20, 0, 0, TimeSpan.Zero),
            Data: data,
            Sequence: seq,
            ReadMonotonicTicks: 0);

    private static LogStreamDriver NewDriverWith(
        ILocalPlayerLogStream? localPlayer = null,
        IClassifiedPlayerLogStream? classified = null,
        IDiagnosticsSink? diag = null)
    {
        var attention = new LogStreamAttentionSource();
        return new LogStreamDriver(
            localPlayer ?? NoopLocalPlayer.Instance,
            NoopCombat.Instance,
            NoopSystem.Instance,
            classified ?? NoopClassified.Instance,
            NoopChat.Instance,
            attention,
            diag);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed > budget) throw new TimeoutException("WaitUntilAsync gave up");
            await Task.Delay(10);
        }
    }

    // ============================================================
    // Test stubs
    // ============================================================

    /// <summary>
    /// Reproduces L0/L0.5's "direct-yield replay buffer THEN bounded-channel
    /// live tail" shape — but on the L1-facing
    /// <see cref="ILocalPlayerLogStream.SubscribeWithReplayMarkerAsync"/>
    /// surface so the IsReplay bit reaches the driver authoritatively.
    /// </summary>
    private sealed class TwoPhaseLocalPlayerStream : ILocalPlayerLogStream
    {
        private readonly List<LocalPlayerLogLine> _replay = new();
        private readonly Channel<LocalPlayerLogLine> _live = Channel.CreateUnbounded<LocalPlayerLogLine>();

        public void AddReplay(LocalPlayerLogLine line) => _replay.Add(line);
        public void PushLive(LocalPlayerLogLine line) => _live.Writer.TryWrite(line);
        public void Complete() => _live.Writer.TryComplete();

        public async IAsyncEnumerable<LocalPlayerLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var line in _replay)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return line;
            }
            await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return line;
        }

        public async IAsyncEnumerable<LogEnvelope<LocalPlayerLogLine>>
            SubscribeWithReplayMarkerAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var line in _replay)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: true);
            }
            await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: false);
        }
    }

    private sealed class ScriptedChatStream : IChatLogStream
    {
        private readonly Channel<RawLogLine> _ch = Channel.CreateUnbounded<RawLogLine>();
        private long _seq;
        public void Push(string line)
        {
            var seq = Interlocked.Increment(ref _seq);
            _ch.Writer.TryWrite(new RawLogLine(
                Timestamp: DateTimeOffset.UtcNow, Line: line,
                Sequence: seq, ReadMonotonicTicks: 0));
        }
        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var line in _ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return line;
        }
    }

    private sealed class NoopLocalPlayer : ILocalPlayerLogStream
    {
        public static readonly NoopLocalPlayer Instance = new();
        public async IAsyncEnumerable<LocalPlayerLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            yield break;
        }
        // SubscribeWithReplayMarkerAsync inherits the DIM thrower — these
        // Noop fakes never drive L1 (no driver subscription is ever made
        // against them in these tests), so the default is fine.
    }

    private sealed class NoopCombat : ICombatActorLogStream
    {
        public static readonly NoopCombat Instance = new();
        public async IAsyncEnumerable<CombatActorLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            yield break;
        }
    }

    private sealed class NoopSystem : ISystemSignalLogStream
    {
        public static readonly NoopSystem Instance = new();
        public async IAsyncEnumerable<SystemSignalLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            yield break;
        }
    }

    private sealed class NoopChat : IChatLogStream
    {
        public static readonly NoopChat Instance = new();
        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            yield break;
        }
    }

    private sealed class NoopClassified : IClassifiedPlayerLogStream
    {
        public static readonly NoopClassified Instance = new();
        public async IAsyncEnumerable<LogEnvelope<IClassifiedPlayerLogLine>> SubscribeWithReplayMarkerAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            yield break;
        }
    }

    /// <summary>
    /// Counts the number of times SubscribeWithReplayMarkerAsync is
    /// enumerated. Used by the Risk 3 dispatch-pin test to verify that
    /// Subscribe&lt;LocalPlayerLogLine&gt; never opens a subscription on
    /// the unified pipe.
    /// </summary>
    private sealed class SubscribeCountingClassifiedStream : IClassifiedPlayerLogStream
    {
        private readonly Channel<LocalPlayerLogLine> _live = Channel.CreateUnbounded<LocalPlayerLogLine>();
        private int _subscribeCount;

        public int SubscribeCount => Volatile.Read(ref _subscribeCount);

        public void PushLive(LocalPlayerLogLine line) => _live.Writer.TryWrite(line);

        public async IAsyncEnumerable<LogEnvelope<IClassifiedPlayerLogLine>> SubscribeWithReplayMarkerAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref _subscribeCount);
            await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return new LogEnvelope<IClassifiedPlayerLogLine>(line, IsReplay: false);
        }
    }

    private sealed class CapturingDiag : IDiagnosticsSink, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<DiagnosticEntry> _entries = new();
        public int WarnCount { get { lock (_gate) return _entries.Count(e => e.Level == DiagnosticLevel.Warn); } }
        public int ErrorCount { get { lock (_gate) return _entries.Count(e => e.Level == DiagnosticLevel.Error); } }
        public int InfoCount { get { lock (_gate) return _entries.Count(e => e.Level == DiagnosticLevel.Info); } }
        public void Write(DiagnosticLevel level, string category, string message)
        {
            lock (_gate) _entries.Add(new DiagnosticEntry(DateTime.UtcNow, level, category, message));
        }
        public IReadOnlyList<DiagnosticEntry> Snapshot() { lock (_gate) return _entries.ToArray(); }
        public event EventHandler<DiagnosticEntry>? EntryAdded { add { } remove { } }
        public void Dispose() { }
    }

    /// <summary>
    /// Single-threaded STA dispatcher for the Marshaled bridge tests. Spins
    /// up a dedicated thread, runs a <see cref="Dispatcher"/> on it, and
    /// exposes <see cref="ThreadId"/> for thread-affinity assertions.
    /// </summary>
    private sealed class StaThread : IDisposable
    {
        public Dispatcher Dispatcher { get; private set; } = null!;
        public int ThreadId { get; private set; }
        private Thread? _thread;

        public static StaThread Start()
        {
            var t = new StaThread();
            t.StartInternal();
            return t;
        }

        private void StartInternal()
        {
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                Dispatcher = Dispatcher.CurrentDispatcher;
                ThreadId = Environment.CurrentManagedThreadId;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "L1DriverTests-Dispatcher",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        public Task<TResult> InvokeAsync<TResult>(Func<TResult> func) =>
            Dispatcher.InvokeAsync(func).Task;
        public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

        public void Shutdown()
        {
            if (Dispatcher is { HasShutdownStarted: false })
            {
                Dispatcher.InvokeAsync(() => Dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal));
            }
            _thread?.Join(TimeSpan.FromSeconds(2));
        }

        public void Dispose() => Shutdown();
    }
}
