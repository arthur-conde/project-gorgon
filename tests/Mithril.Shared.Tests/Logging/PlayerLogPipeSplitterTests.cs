using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests.Logging;

/// <summary>
/// L0.5 (#556) <see cref="PlayerLogPipeSplitter"/> unit tests. The
/// splitter is tested in isolation via a controllable
/// <see cref="IClassifiedPlayerLogStream"/> stub so the test
/// (a) doesn't depend on the classifier's own behaviour (covered by
/// <see cref="PlayerLogClassifierTests"/>) and
/// (b) doesn't depend on concurrent task scheduling under parallel-test
/// pressure. The full classifier + splitter end-to-end behaviour is
/// covered by <see cref="PlayerLogPipelineTests"/>.
/// </summary>
public sealed class PlayerLogPipeSplitterTests
{
    private static readonly DateTimeOffset Ts =
        new(2026, 5, 19, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Union_of_typed_pipes_equals_unified_pipe_input()
    {
        // Feed a mixed batch of Local/Combat/System envelopes into the
        // splitter. The union of the three typed pipes' output must equal
        // the unified pipe's input set (by Sequence) — the splitter's
        // faithful-projection contract.
        var classified = new StubClassifiedStream();
        using var splitter = new PlayerLogPipeSplitter(classified);

        var localTask = CollectAsync(((ILocalPlayerLogStream)splitter).SubscribeAsync, 3);
        var combatTask = CollectAsync(((ICombatActorLogStream)splitter).SubscribeAsync, 1);
        var systemTask = CollectAsync(((ISystemSignalLogStream)splitter).SubscribeAsync, 2);

        var inputs = new IClassifiedPlayerLogLine[]
        {
            new SystemSignalLogLine(Ts, SystemSignalKind.AreaLoading, "AreaKurMountains", 1, 0),
            new LocalPlayerLogLine(Ts, "ProcessAddItem(A)", 2, 0),
            new LocalPlayerLogLine(Ts, "ProcessAddItem(B)", 3, 0),
            new CombatActorLogLine(Ts, 25021745, "OnAttackHitMe(X)", 4, 0),
            new LocalPlayerLogLine(Ts, "ProcessRemoveEffects(Y)", 5, 0),
            new SystemSignalLogLine(Ts, SystemSignalKind.LoginBanner, "banner", 6, 0),
        };
        foreach (var line in inputs) classified.PushLive(line);

        var local = await localTask.WaitAsync(TimeSpan.FromSeconds(15));
        var combat = await combatTask.WaitAsync(TimeSpan.FromSeconds(15));
        var system = await systemTask.WaitAsync(TimeSpan.FromSeconds(15));

        // Union of typed pipes equals unified pipe input (set equality by Sequence).
        var unionSeqs = local.Select(x => x.Sequence)
            .Concat(combat.Select(x => x.Sequence))
            .Concat(system.Select(x => x.Sequence))
            .OrderBy(x => x)
            .ToList();
        var inputSeqs = inputs.Select(x => x.Sequence).OrderBy(x => x).ToList();
        unionSeqs.Should().Equal(inputSeqs);

        // Per-typed-pipe: monotonic Sequence ordering preserved.
        local.Select(x => x.Sequence).Should().BeInAscendingOrder();
        combat.Select(x => x.Sequence).Should().BeInAscendingOrder();
        system.Select(x => x.Sequence).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Faithful_projection_typed_pipe_yields_same_instance_as_input()
    {
        // The splitter dispatches the SAME instance to typed pipes — it
        // doesn't allocate a new record on dispatch. This pins the
        // faithful-projection invariant at the type-system level: a future
        // refactor that wrapped/copied the record would be caught here.
        var classified = new StubClassifiedStream();
        using var splitter = new PlayerLogPipeSplitter(classified);

        var localTask = CollectAsync(((ILocalPlayerLogStream)splitter).SubscribeAsync, 3);

        var a = new LocalPlayerLogLine(Ts, "ProcessAddItem(A)", 1, 0);
        var b = new LocalPlayerLogLine(Ts, "ProcessAddItem(B)", 2, 0);
        var c = new LocalPlayerLogLine(Ts, "ProcessAddItem(C)", 3, 0);
        classified.PushLive(a);
        classified.PushLive(b);
        classified.PushLive(new CombatActorLogLine(Ts, 1, "OnX", 4, 0)); // not Local — filtered
        classified.PushLive(c);

        var typedLocal = await localTask.WaitAsync(TimeSpan.FromSeconds(15));

        typedLocal.Should().HaveCount(3);
        ReferenceEquals(typedLocal[0], a).Should().BeTrue();
        ReferenceEquals(typedLocal[1], b).Should().BeTrue();
        ReferenceEquals(typedLocal[2], c).Should().BeTrue();
    }

    [Fact]
    public async Task Marker_variant_forwards_IsReplay_bit_unchanged()
    {
        // The splitter's marker variant must forward the IsReplay bit
        // from the unified pipe unchanged onto the typed-pipe envelope.
        var classified = new StubClassifiedStream();
        using var splitter = new PlayerLogPipeSplitter(classified);

        var collectTask = CollectMarkerAsync(
            ((ILocalPlayerLogStream)splitter).SubscribeWithReplayMarkerAsync, 3);

        classified.Push(new LocalPlayerLogLine(Ts, "REPLAY1", 1, 0), isReplay: true);
        classified.Push(new LocalPlayerLogLine(Ts, "REPLAY2", 2, 0), isReplay: true);
        classified.Push(new LocalPlayerLogLine(Ts, "LIVE", 3, 0), isReplay: false);

        var collected = await collectTask.WaitAsync(TimeSpan.FromSeconds(15));

        collected.Select(e => e.IsReplay).Should().Equal(true, true, false);
        collected.Select(e => e.Payload.Data).Should().Equal("REPLAY1", "REPLAY2", "LIVE");
    }

    [Fact]
    public async Task Combat_envelopes_on_unified_pipe_do_not_reach_local_typed_pipe()
    {
        // The Pin/Weather/Position unified-pipe subscribers will receive
        // CombatActorLogLine envelopes and no-op on them. The reciprocal
        // direction is also true for the typed pipes: subscribing to the
        // local typed pipe never observes combat envelopes — proves
        // dispatch is by runtime type, not "everything that implements
        // IClassifiedPlayerLogLine".
        var classified = new StubClassifiedStream();
        using var splitter = new PlayerLogPipeSplitter(classified);

        var localTask = CollectAsync(((ILocalPlayerLogStream)splitter).SubscribeAsync, 2);

        classified.PushLive(new LocalPlayerLogLine(Ts, "L1", 1, 0));
        classified.PushLive(new CombatActorLogLine(Ts, 1, "OnX", 2, 0));
        classified.PushLive(new LocalPlayerLogLine(Ts, "L2", 3, 0));

        var local = await localTask.WaitAsync(TimeSpan.FromSeconds(15));

        local.Select(x => x.Data).Should().Equal("L1", "L2");
    }

    [Fact]
    public async Task Unknown_payload_type_lands_on_default_branch_and_increments_DispatchFailures()
    {
        // The splitter's switch covers the three concrete IClassifiedPlayerLogLine
        // implementers; any other implementer (a future fourth typed pipe,
        // or a test-only stub) must fall through to the default: branch,
        // increment _dispatchFailures, and NOT crash the pump or starve
        // typed-pipe subscribers. Risk-1 pin from the #556 brainstorm.
        var classified = new StubClassifiedStream();
        using var splitter = new PlayerLogPipeSplitter(classified);

        // Open a typed subscription so the splitter starts its pump.
        var localTask = CollectAsync(((ILocalPlayerLogStream)splitter).SubscribeAsync, 1);

        // Mix one unknown payload with one valid LocalPlayerLogLine. The
        // unknown one should be counted-and-warned; the Local one should
        // still reach the typed subscriber.
        classified.PushLive(new UnknownClassifiedPayload(Ts, "junk", Sequence: 1));
        classified.PushLive(new LocalPlayerLogLine(Ts, "ProcessOk", 2, 0));

        var local = await localTask.WaitAsync(TimeSpan.FromSeconds(15));
        local.Should().ContainSingle().Which.Data.Should().Be("ProcessOk");

        splitter.Counters.DispatchFailures.Should().Be(1,
            because: "the unknown-typed envelope hit the default: branch");
    }

    [Fact]
    public async Task Long_backlog_through_typed_pipe_delivers_every_envelope()
    {
        // Regression test for the silent-drop bug surfaced during
        // world-sim debugging. Pre-fix, each per-subscriber typed-pipe
        // channel was bounded at 1024 with FullMode=DropOldest, so a
        // long-session cold-start replay that dispatched >1024
        // LocalPlayer lines faster than the subscriber's pump could
        // drain would silently evict the earliest items. The concrete
        // failure: the PlayerInventoryFrameProducer's L1 subscription
        // received the live ProcessCombatModeStatus tail of a 12-minute
        // session but had its ~134-item ProcessAddItem replay block
        // evicted, which left its iterator channel empty, which left
        // the world merger blocked on inv.PendingFetch, which starved
        // every downstream producer (notably the area producer — the
        // user-visible symptom was "area changes don't reach Palantir
        // or Legolas").
        //
        // Post-fix the channel is unbounded — every envelope arrives.
        var classified = new StubClassifiedStream();
        using var splitter = new PlayerLogPipeSplitter(classified);

        const int count = 5000; // ~5× the previous 1024 capacity
        for (var i = 1; i <= count; i++)
        {
            classified.PushLive(new LocalPlayerLogLine(Ts, $"L{i}", i, 0));
        }

        var received = await CollectAsync(
            ((ILocalPlayerLogStream)splitter).SubscribeAsync, count,
            TimeSpan.FromSeconds(30));

        received.Should().HaveCount(count, because: "no envelope may be silently dropped under a long replay backlog");
        received.Select(x => x.Sequence).Should().BeInAscendingOrder();
        received.Select(x => x.Sequence).Should().Equal(
            Enumerable.Range(1, count).Select(i => (long)i),
            because: "every Sequence in the input set must reach the subscriber");
    }

    [Fact]
    public async Task Long_backlog_through_marker_variant_delivers_every_envelope()
    {
        // Same regression, marker variant (the L1-driver-facing path).
        var classified = new StubClassifiedStream();
        using var splitter = new PlayerLogPipeSplitter(classified);

        const int count = 5000;
        for (var i = 1; i <= count; i++)
        {
            classified.Push(new LocalPlayerLogLine(Ts, $"L{i}", i, 0), isReplay: true);
        }

        var received = await CollectMarkerAsync(
            ((ILocalPlayerLogStream)splitter).SubscribeWithReplayMarkerAsync, count,
            TimeSpan.FromSeconds(30));

        received.Should().HaveCount(count, because: "no envelope may be silently dropped under a long replay backlog");
        received.Select(e => e.Payload.Sequence).Should().Equal(
            Enumerable.Range(1, count).Select(i => (long)i),
            because: "every Sequence in the input set must reach the subscriber, in source order");
        received.Should().OnlyContain(e => e.IsReplay,
            because: "the marker bit forwarded unchanged from the upstream-replay envelopes");
    }

    [Fact]
    public async Task Handler_throw_in_typed_subscriber_does_not_kill_splitter_pump()
    {
        // The splitter's per-envelope try/catch contains Dispatch throws so
        // one bad envelope doesn't take down the pump. We can't easily
        // make the production Dispatch throw, but we can verify the
        // surrounding lifecycle: a typed-pipe subscriber that throws gets
        // its containment from the L1 driver's bridge — not from the
        // splitter — so the splitter's role is structural (dispatch by
        // type) rather than per-handler. This test pins the structural
        // guarantee: the splitter survives a downstream channel that
        // refuses writes (closed-channel TryWrite returns false silently).
        var classified = new StubClassifiedStream();
        using var splitter = new PlayerLogPipeSplitter(classified);

        // Open then close a typed subscription's channel by disposing the
        // CTS mid-flight. Subsequent writes to that channel TryWrite-fail
        // silently — the splitter's PublishLocal must NOT throw.
        using var firstCts = new CancellationTokenSource();
        var pendingTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in ((ILocalPlayerLogStream)splitter).SubscribeAsync(firstCts.Token))
                {
                    // Cancel on first item so the subscription unwinds.
                    firstCts.Cancel();
                }
            }
            catch (OperationCanceledException) { /* expected */ }
        });
        classified.PushLive(new LocalPlayerLogLine(Ts, "FIRST", 1, 0));
        await pendingTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Now push more envelopes. The first subscriber's channel is gone;
        // the splitter must not throw, and a fresh subscription must still
        // receive items.
        var afterTask = CollectAsync(((ILocalPlayerLogStream)splitter).SubscribeAsync, 1);
        classified.PushLive(new LocalPlayerLogLine(Ts, "AFTER", 2, 0));
        var after = await afterTask.WaitAsync(TimeSpan.FromSeconds(5));
        after.Should().ContainSingle().Which.Data.Should().Be("AFTER");

        splitter.Counters.DispatchFailures.Should().Be(0,
            because: "a closed/abandoned downstream channel is not a dispatch failure");
    }

    private static async Task<List<T>> CollectAsync<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> subscribe,
        int expected,
        TimeSpan? timeBudget = null)
    {
        var budget = timeBudget ?? TimeSpan.FromSeconds(15);
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
        catch (OperationCanceledException) { }
        return result;
    }

    private static async Task<List<LogEnvelope<LocalPlayerLogLine>>>
        CollectMarkerAsync(
            Func<CancellationToken, IAsyncEnumerable<LogEnvelope<LocalPlayerLogLine>>> subscribe,
            int expected,
            TimeSpan? timeBudget = null)
    {
        var budget = timeBudget ?? TimeSpan.FromSeconds(15);
        using var cts = new CancellationTokenSource(budget);
        var result = new List<LogEnvelope<LocalPlayerLogLine>>();
        try
        {
            await foreach (var item in subscribe(cts.Token).ConfigureAwait(false))
            {
                result.Add(item);
                if (result.Count >= expected) break;
            }
        }
        catch (OperationCanceledException) { }
        return result;
    }

    /// <summary>
    /// A test-only fourth implementer of <see cref="IClassifiedPlayerLogLine"/>
    /// — none of the splitter's three switch arms match it, so it should
    /// land on the <c>default:</c> branch and increment
    /// <see cref="PlayerLogPipeSplitter.SplitterCounters.DispatchFailures"/>
    /// without killing the pump. Pins the closed-set assumption documented
    /// on <see cref="IClassifiedPlayerLogLine"/>.
    /// </summary>
    private sealed record UnknownClassifiedPayload(
        DateTimeOffset Timestamp,
        string Data,
        long Sequence,
        long ReadMonotonicTicks = 0,
        string? Raw = null) : IClassifiedPlayerLogLine;

    /// <summary>
    /// In-memory <see cref="IClassifiedPlayerLogStream"/> the splitter can
    /// subscribe to. Push items via <see cref="PushLive"/> or
    /// <see cref="Push"/>; the splitter receives them through its single
    /// subscription's bounded channel. Decouples the splitter test from
    /// the classifier's own lifecycle so the test exercises only the
    /// splitter's behaviour.
    /// </summary>
    private sealed class StubClassifiedStream : IClassifiedPlayerLogStream
    {
        private readonly Channel<LogEnvelope<IClassifiedPlayerLogLine>> _ch =
            Channel.CreateUnbounded<LogEnvelope<IClassifiedPlayerLogLine>>();

        public void PushLive(IClassifiedPlayerLogLine line) =>
            _ch.Writer.TryWrite(new LogEnvelope<IClassifiedPlayerLogLine>(line, IsReplay: false));

        public void Push(IClassifiedPlayerLogLine line, bool isReplay) =>
            _ch.Writer.TryWrite(new LogEnvelope<IClassifiedPlayerLogLine>(line, isReplay));

        public async IAsyncEnumerable<LogEnvelope<IClassifiedPlayerLogLine>> SubscribeWithReplayMarkerAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            while (await _ch.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_ch.Reader.TryRead(out var env))
                    yield return env;
            }
        }
    }
}
