using FluentAssertions;
using Mithril.GameState.Inventory;
using Mithril.GameState.Inventory.Producers;
using Mithril.GameState.Tests.TestSupport;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;
using Xunit;

namespace Mithril.GameState.Tests.Inventory;

/// <summary>
/// End-to-end tests for the world-sim inventory split (#602) — wires
/// <see cref="PlayerInventoryFrameProducer"/> → <see cref="PlayerWorld"/> →
/// <see cref="PlayerInventoryStateService"/> folder → <see cref="IWorldEventBus"/>
/// and asserts the pipeline delivers <see cref="PlayerInventoryAdded"/> /
/// <see cref="PlayerInventoryRemoved"/> / <see cref="PlayerInventoryStackUpdated"/>
/// emissions on the world's bus in source order. Mirrors
/// <c>SkillFolderEndToEndTests</c> (the Phase 1 #618 template).
/// </summary>
public sealed class PlayerInventoryFolderEndToEndTests
{
    private static DateTime Ts(int h, int m, int s) =>
        new(2026, 5, 22, h, m, s, DateTimeKind.Utc);

    [Fact]
    public async Task Replay_envelopes_flow_through_producer_world_folder_to_bus_subscribers()
    {
        using var driver = new TestLogStreamDriver();
        var folder = new PlayerInventoryStateService();
        var producer = new PlayerInventoryFrameProducer(driver);
        var world = new PlayerWorld();

        // PG replay: two AddItems + one DeleteItem in source order.
        driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(
            "[08:22:21] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)", Ts(8, 22, 21)));
        driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(
            "[08:22:22] LocalPlayer: ProcessAddItem(BarleySeeds(7), -1, True)", Ts(8, 22, 22)));
        driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(
            "[08:30:00] LocalPlayer: ProcessDeleteItem(42)", Ts(8, 30, 0)));

        world.RegisterProducer(producer);
        world.RegisterFolder(folder);

        var added = new List<Frame<PlayerInventoryAdded>>();
        var removed = new List<Frame<PlayerInventoryRemoved>>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int expectedTotal = 3;
        using var _add = world.Bus.Subscribe<PlayerInventoryAdded>(f =>
        {
            added.Add(f);
            if (added.Count + removed.Count >= expectedTotal) allReceived.TrySetResult();
        });
        using var _rm = world.Bus.Subscribe<PlayerInventoryRemoved>(f =>
        {
            removed.Add(f);
            if (added.Count + removed.Count >= expectedTotal) allReceived.TrySetResult();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = world.StartMerger(cts.Token);

        await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));
        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        added.Should().HaveCount(2);
        added.Select(f => f.Payload.InstanceId).Should().Equal(new[] { 42L, 7L });
        added.Select(f => f.Payload.InternalName).Should().Equal(new[] { "Moonstone", "BarleySeeds" });

        removed.Should().ContainSingle();
        removed[0].Payload.InstanceId.Should().Be(42L);
        removed[0].Payload.InternalName.Should().Be("Moonstone");

        // Retained-on-delete: the folder ledger keeps the resolver mapping
        // after a remove, so consumers reading the snapshot after the
        // delete still see the InternalName (the Arwen attribution flow).
        folder.TryResolve(42, out var n).Should().BeTrue();
        n.Should().Be("Moonstone");
    }

    [Fact]
    public async Task Two_runs_against_identical_replay_emit_identical_frames()
    {
        // Replay-determinism contract (#602 acceptance): two runs over the
        // same recorded line corpus must produce byte-identical bus
        // trajectories. This is the system-level invariant the world-sim
        // architecture exists to enforce — the folder + producer pipeline
        // is a pure function of the source stream.
        async Task<List<(long InstanceId, string Name, string Kind)>> RunOnce()
        {
            using var driver = new TestLogStreamDriver();
            var folder = new PlayerInventoryStateService();
            var producer = new PlayerInventoryFrameProducer(driver);
            var world = new PlayerWorld();

            driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(
                "[08:22:21] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)", Ts(8, 22, 21)));
            driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(
                "[08:30:00] LocalPlayer: ProcessUpdateItemCode(42, 262144, True)", Ts(8, 30, 0)));
            driver.PushReplay(TestLogEnvelopeFactory.FromRawLine(
                "[08:31:00] LocalPlayer: ProcessDeleteItem(42)", Ts(8, 31, 0)));

            world.RegisterProducer(producer);
            world.RegisterFolder(folder);

            var observed = new List<(long, string, string)>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const int expectedTotal = 3;
            void Maybe() { if (observed.Count >= expectedTotal) done.TrySetResult(); }

            using var _add = world.Bus.Subscribe<PlayerInventoryAdded>(f =>
            {
                observed.Add((f.Payload.InstanceId, f.Payload.InternalName, "Added"));
                Maybe();
            });
            using var _upd = world.Bus.Subscribe<PlayerInventoryStackUpdated>(f =>
            {
                observed.Add((f.Payload.InstanceId, f.Payload.InternalName, $"Stack:{f.Payload.StackSize}"));
                Maybe();
            });
            using var _rm = world.Bus.Subscribe<PlayerInventoryRemoved>(f =>
            {
                observed.Add((f.Payload.InstanceId, f.Payload.InternalName, "Removed"));
                Maybe();
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var run = world.StartMerger(cts.Token);
            await driver.DrainLocalPlayerAsync(TimeSpan.FromSeconds(5));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            return observed;
        }

        var run1 = await RunOnce();
        var run2 = await RunOnce();
        run2.Should().Equal(run1, "world-sim folder pipeline must be a pure function of the source stream");
        run1.Should().Equal(new[]
        {
            (42L, "Moonstone", "Added"),
            (42L, "Moonstone", "Stack:5"),  // (4 << 16) | _ → size 5
            (42L, "Moonstone", "Removed"),
        });
    }
}
