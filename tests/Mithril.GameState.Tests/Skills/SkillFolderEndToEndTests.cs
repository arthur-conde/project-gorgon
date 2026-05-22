using FluentAssertions;
using Mithril.GameState.Skills;
using Mithril.GameState.Skills.Parsing;
using Mithril.GameState.Skills.Producers;
using Mithril.GameState.Tests.TestSupport;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;
using Xunit;

namespace Mithril.GameState.Tests.Skills;

/// <summary>
/// End-to-end tests that wire <see cref="SkillFrameProducer"/> →
/// <see cref="PlayerWorld"/> → <see cref="PlayerSkillStateService"/> folder →
/// <see cref="IWorldEventBus"/> together, and assert the pipeline delivers
/// <see cref="SkillChange"/> emissions on the world's bus in source order
/// (issue #618 — Phase 1 acceptance: "New test exercises the IPlayerWorld →
/// folder → bus pipeline end-to-end"). The folder-side semantics (snapshot
/// mutation, reference enrichment) are pinned by
/// <see cref="PlayerSkillStateServiceTests"/>; this file is about wiring.
/// </summary>
public sealed class SkillFolderEndToEndTests
{
    private const string LoadLine =
        "[08:22:21] LocalPlayer: ProcessLoadSkills(" +
        "{type=Toolcrafting,raw=15,bonus=0,xp=26,tnl=680,max=50}, " +
        "{type=Tanning,raw=50,bonus=3,xp=0,tnl=5280,max=50})";

    private const string LiveUpdateLine =
        "[08:30:00] LocalPlayer: ProcessUpdateSkill(" +
        "{type=Toolcrafting,raw=16,bonus=0,xp=5,tnl=700,max=50}, True, 4, 0, 0)";

    private const string LiveNewSkillLine =
        "[08:31:00] LocalPlayer: ProcessUpdateSkill(" +
        "{type=Sword,raw=2,bonus=0,xp=1,tnl=9,max=50}, True, 1, 0, 0)";

    private static DateTime Ts(int h, int m, int s) =>
        new(2026, 5, 22, h, m, s, DateTimeKind.Utc);

    [Fact]
    public async Task Replay_envelopes_flow_through_producer_world_folder_to_bus_subscribers()
    {
        using var driver = new TestLogStreamDriver();
        var folder = new PlayerSkillStateService();
        var producer = new SkillFrameProducer(driver, new SkillLogParser());
        var world = new PlayerWorld();

        // Push everything as REPLAY before start so the producer's L1 callback
        // sees them as replay envelopes — the test exercises the cold-start
        // backlog drain end-to-end.
        driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(LoadLine, Ts(8, 22, 21)));
        driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(LiveUpdateLine, Ts(8, 30, 0)));

        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        // Two expected emissions: one SnapshotReplace (Toolcrafting from
        // LoadLine) + Tanning (capped, also SnapshotReplace) plus the Delta
        // from the LiveUpdateLine. The folder's diff against PlayerSkillSnapshot.Empty
        // emits one SnapshotReplace per skill in the snapshot.
        var observed = new List<Frame<SkillChange>>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int expectedCount = 3;
        using var _sub = world.Bus.Subscribe<SkillChange>(frame =>
        {
            observed.Add(frame);
            if (observed.Count >= expectedCount) allReceived.TrySetResult();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = world.StartMerger(cts.Token);

        // Drive the L1 pump to drain its backlog into the producer's channel.
        await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));

        // Wait until all expected SkillChange emissions reach the bus
        // (the world's merger pulls frames from the producer's channel and
        // dispatches them; the bus subscriber appends synchronously).
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Tear the world down cleanly — the producer's channel never naturally
        // completes (the L1 driver stays open), so cancellation is the only
        // way out.
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { /* expected */ }

        observed.Should().HaveCount(expectedCount);

        // Order matters: SnapshotReplace events for Toolcrafting + Tanning fire
        // FIRST (in log order — the snapshot's diff visits the dictionary
        // built from snap.Skills, which iterates insertion order on
        // <Dictionary> in netcoreapp+), then the Delta from the live update.
        // Asserting the multiset rather than the dictionary-iteration order
        // keeps the test stable if the folder's diff ever switches to
        // a sorted projection.
        var snapshotChanges = observed
            .Where(f => f.Payload.Kind == SkillChangeKind.SnapshotReplace)
            .ToList();
        var deltaChanges = observed
            .Where(f => f.Payload.Kind == SkillChangeKind.Delta)
            .ToList();

        snapshotChanges.Select(f => f.Payload.SkillKey).Should().BeEquivalentTo(
            new[] { "Toolcrafting", "Tanning" });
        deltaChanges.Should().ContainSingle();
        deltaChanges[0].Payload.SkillKey.Should().Be("Toolcrafting");
        deltaChanges[0].Payload.Current.Level.Should().Be(16);
        deltaChanges[0].Payload.XpGained.Should().Be(4);

        // Frame timestamps preserve source order — Delta must follow the
        // snapshot in event-time.
        var snapshotTs = snapshotChanges[0].Timestamp;
        deltaChanges[0].Timestamp.Should().BeOnOrAfter(snapshotTs);
    }

    [Fact]
    public async Task Live_envelopes_after_replay_flip_world_to_Live_and_reach_bus_subscribers()
    {
        using var driver = new TestLogStreamDriver();
        var folder = new PlayerSkillStateService();
        var producer = new SkillFrameProducer(driver, new SkillLogParser());
        var world = new PlayerWorld();

        // Replay phase: one snapshot frame.
        driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(LoadLine, Ts(8, 22, 21)));

        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var observed = new List<Frame<SkillChange>>();
        var observedModes = new List<WorldMode>();
        var liveDeltaReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modeChanges = new List<Frame<ModeChanged>>();
        using var _sub = world.Bus.Subscribe<SkillChange>(frame =>
        {
            observed.Add(frame);
            observedModes.Add(world.Clock.Mode);
            if (frame.Payload.Kind == SkillChangeKind.Delta) liveDeltaReceived.TrySetResult();
        });
        using var _modeSub = world.Bus.Subscribe<ModeChanged>(modeChanges.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = world.StartMerger(cts.Token);

        // Drain the replay phase first; the producer signals ReachedLive
        // immediately on the first non-replay envelope it sees, so we push the
        // live one AFTER replay drains to keep the mode boundary deterministic.
        await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));

        driver.PushLive(TestLogEnvelopeFactory.FromRawLine(LiveNewSkillLine, Ts(8, 31, 0)));
        await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));
        await liveDeltaReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        // The live Sword delta is observable on the bus.
        var swordChange = observed.Should()
            .Contain(f => f.Payload.SkillKey == "Sword").Subject;
        swordChange.Payload.Kind.Should().Be(SkillChangeKind.Delta);
        swordChange.Payload.Current.Level.Should().Be(2);
        swordChange.Payload.XpGained.Should().Be(1);

        // Mode flipped: at least one ModeChanged emission on the bus (the
        // world fires it between dispatches once every mode-aware producer's
        // ReachedLive completes), and the live delta dispatched in Live mode.
        modeChanges.Should().NotBeEmpty();
        modeChanges[0].Payload.From.Should().Be(WorldMode.Replaying);
        modeChanges[0].Payload.To.Should().Be(WorldMode.Live);

        // The mode-tagged observation at the time the Sword delta fired must
        // be Live — that's the whole point of the mode bit (side-effect
        // consumers gate on it).
        var swordIndex = observed.FindIndex(f => f.Payload.SkillKey == "Sword");
        observedModes[swordIndex].Should().Be(WorldMode.Live);

        world.Clock.Mode.Should().Be(WorldMode.Live);
    }

    [Fact]
    public async Task Folder_state_is_observable_via_legacy_IPlayerSkillState_after_bus_dispatch()
    {
        // Back-compat: the folder also serves as IPlayerSkillState. Existing
        // consumers (Smaug snapshot-Subscribe, Samwise SubscribeChanges)
        // see the SAME content the bus carries. Pin both surfaces on one
        // exercised pipeline.
        using var driver = new TestLogStreamDriver();
        var folder = new PlayerSkillStateService();
        var producer = new SkillFrameProducer(driver, new SkillLogParser());
        var world = new PlayerWorld();

        driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(LoadLine, Ts(8, 22, 21)));

        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var busChanges = new List<SkillChange>();
        var legacyChanges = new List<SkillChange>();
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int expectedCount = 2; // Toolcrafting + Tanning SnapshotReplace from LoadLine

        using var _bus = world.Bus.Subscribe<SkillChange>(f =>
        {
            busChanges.Add(f.Payload);
            if (busChanges.Count >= expectedCount) allDelivered.TrySetResult();
        });
        using var _legacy = ((IPlayerSkillState)folder).SubscribeChanges(legacyChanges.Add);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = world.StartMerger(cts.Token);

        await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));
        await allDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        busChanges.Should().BeEquivalentTo(legacyChanges,
            "the world bus and the legacy SubscribeChanges channel must deliver identical content");

        // IPlayerSkillState.Current carries the folder's current state — read
        // via the legacy surface that Smaug and other snapshot consumers use.
        ((IPlayerSkillState)folder).Current.Skills
            .Should().ContainKeys("Toolcrafting", "Tanning");
    }
}
