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
