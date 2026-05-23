using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Areas.Producers;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Logging;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;
using Xunit;

namespace Mithril.GameState.Tests.Areas;

/// <summary>
/// End-to-end tests that wire <see cref="AreaLoadingFrameProducer"/> →
/// <see cref="PlayerWorld"/> → <see cref="PlayerAreaTracker"/> folder →
/// <see cref="IWorldEventBus"/> together (#775). Pins the replay-determinism
/// contract: N AreaLoading envelopes through the pipeline yield exactly N
/// <see cref="PlayerAreaChanged"/> events on the world bus, with timestamps
/// drawn from the L1 envelopes (NOT from <see cref="DateTime.UtcNow"/>).
/// </summary>
public sealed class PlayerAreaWorldIntegrationTests
{
    [Fact]
    public async Task Replay_N_portal_transitions_yield_N_PlayerAreaChanged_events_with_log_timestamps()
    {
        // Fixture: three distinct area transitions in replay order. The
        // pipeline must surface three PlayerAreaChanged emissions on the
        // bus, each timestamped from the source envelope's log instant.
        using var driver = new TestLogStreamDriver();
        var ts1 = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2026, 5, 23, 14, 35, 0, TimeSpan.Zero);
        var ts3 = new DateTimeOffset(2026, 5, 23, 14, 40, 0, TimeSpan.Zero);
        driver.PushReplay(MakeAreaLoading("AreaSerbule", ts1, seq: 1));
        driver.PushReplay(MakeAreaLoading("AreaEltibule", ts2, seq: 2));
        driver.PushReplay(MakeAreaLoading("AreaTomb1", ts3, seq: 3));

        var folder = new PlayerAreaTracker(new AreaTransitionParser());
        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(), config: null);
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var observed = new List<Frame<PlayerAreaChanged>>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int expectedCount = 3;
        using var _sub = world.Bus.Subscribe<PlayerAreaChanged>(frame =>
        {
            observed.Add(frame);
            if (observed.Count >= expectedCount) allReceived.TrySetResult();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = world.StartMerger(cts.Token);

        await driver.DrainSystemAsync(TimeSpan.FromSeconds(5));
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        observed.Should().HaveCount(expectedCount);

        // Timestamps reflect log-line instants, NOT wall-clock — replay-
        // determinism contract per principle 5 + principle 13.
        observed[0].Timestamp.Should().Be(ts1);
        observed[1].Timestamp.Should().Be(ts2);
        observed[2].Timestamp.Should().Be(ts3);
        observed[0].Payload.Should().BeEquivalentTo(new PlayerAreaChanged(
            PlayerAreaChangeKind.Changed, null, "AreaSerbule", ts1));
        observed[1].Payload.Should().BeEquivalentTo(new PlayerAreaChanged(
            PlayerAreaChangeKind.Changed, "AreaSerbule", "AreaEltibule", ts2));
        observed[2].Payload.Should().BeEquivalentTo(new PlayerAreaChanged(
            PlayerAreaChangeKind.Changed, "AreaEltibule", "AreaTomb1", ts3));

        // Snapshot-kind events are synthesized inside Subscribe and never
        // cross the world boundary — the bus carries Changed only.
        observed.Should().OnlyContain(f => f.Payload.Kind == PlayerAreaChangeKind.Changed);

        // CurrentArea is preserved as the synchronous read surface for the
        // back-compat consumers (Gandalf / Palantir).
        folder.CurrentArea.Should().Be("AreaTomb1");
    }

    [Fact]
    public async Task Re_emitted_unchanged_AreaLoading_envelopes_do_not_re_fire_change_events()
    {
        // PG re-emits the current area's LOADING LEVEL on every login / zone
        // replay; that re-emission must be a folder-state no-op so consumers
        // stay quiet through the L1 backlog.
        using var driver = new TestLogStreamDriver();
        var ts1 = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2026, 5, 23, 14, 30, 5, TimeSpan.Zero);
        var ts3 = new DateTimeOffset(2026, 5, 23, 14, 35, 0, TimeSpan.Zero);
        driver.PushReplay(MakeAreaLoading("AreaSerbule", ts1, seq: 1));
        driver.PushReplay(MakeAreaLoading("AreaSerbule", ts2, seq: 2));   // duplicate
        driver.PushReplay(MakeAreaLoading("AreaEltibule", ts3, seq: 3));

        var folder = new PlayerAreaTracker(new AreaTransitionParser());
        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(), config: null);
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var observed = new List<Frame<PlayerAreaChanged>>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int expectedCount = 2;
        using var _sub = world.Bus.Subscribe<PlayerAreaChanged>(frame =>
        {
            observed.Add(frame);
            if (observed.Count >= expectedCount) allReceived.TrySetResult();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = world.StartMerger(cts.Token);

        await driver.DrainSystemAsync(TimeSpan.FromSeconds(5));
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        // Exactly two events — duplicate AreaSerbule envelope was a no-op.
        observed.Should().HaveCount(2);
        observed[0].Timestamp.Should().Be(ts1);
        observed[1].Timestamp.Should().Be(ts3);
    }

    [Fact]
    public async Task Observe_then_Apply_double_feed_emits_only_zero_bus_frames_for_that_transition()
    {
        // Pins the bus-emission asymmetry across the double-feed window
        // (PlayerAreaTracker XML doc — "Asymmetry: bus emission is NOT
        // idempotent across the double-feed"). State + Subscribe callbacks
        // are unaffected; only the bus count differs depending on which
        // path lands first. This test exercises the Observe-first case:
        // Observe mutates state to AreaSerbule; the producer's later
        // AreaLoading envelope for the same area is a folder no-op (state
        // already matches) and the bus emits ZERO frames for that
        // transition. Captures the asymmetry so a future audit doesn't
        // experience it as a surprise.
        using var driver = new TestLogStreamDriver();
        var folder = new PlayerAreaTracker(new AreaTransitionParser());
        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(), config: null);
        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        // Observe lands FIRST — outside the world's merger, mutates state
        // directly. Simulates a Legolas/Gandalf bridge call arriving before
        // the world's drain reaches the corresponding L1 envelope.
        var when = new DateTime(2026, 5, 23, 14, 30, 0, DateTimeKind.Utc);
        folder.Observe("LOADING LEVEL AreaSerbule", when);
        folder.CurrentArea.Should().Be("AreaSerbule");

        // Now the producer queues the SAME transition via L1. The folder's
        // Apply sees state already matches and returns empty — no bus frame.
        var ts = new DateTimeOffset(when, TimeSpan.Zero);
        driver.PushReplay(MakeAreaLoading("AreaSerbule", ts, seq: 1));

        var busFrames = new List<Frame<PlayerAreaChanged>>();
        using var _sub = world.Bus.Subscribe<PlayerAreaChanged>(busFrames.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = world.StartMerger(cts.Token);
        await driver.DrainSystemAsync(TimeSpan.FromSeconds(5));

        // Brief settle window — the merger dispatches the no-op frame
        // through the folder but the folder returns empty, so nothing
        // surfaces on the bus.
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        busFrames.Should().BeEmpty(
            because: "Observe-first wins the state race; the subsequent " +
            "Apply on the same area returns empty and the bus emits zero.");
        folder.CurrentArea.Should().Be("AreaSerbule");
    }

    private static SystemSignalLogLine MakeAreaLoading(string area, DateTimeOffset ts, long seq) =>
        new(Timestamp: ts, Kind: SystemSignalKind.AreaLoading,
            Data: $"LOADING LEVEL {area}", Sequence: seq, ReadMonotonicTicks: 0);
}
