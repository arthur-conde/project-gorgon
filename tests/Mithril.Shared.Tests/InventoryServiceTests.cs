using System.Threading.Channels;
using FluentAssertions;
using Mithril.Shared.Inventory;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests;

public sealed class InventoryServiceTests
{
    [Fact]
    public async Task ObservesProcessAddItem_PopulatesMap()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessAddItem(AppleJuice(99), -1, False)");
        var svc = new InventoryService(stream);
        await RunUntilDrainedAsync(svc, stream);

        svc.TryResolve(42, out var a).Should().BeTrue();
        a.Should().Be("Moonstone");
        svc.TryResolve(99, out var b).Should().BeTrue();
        b.Should().Be("AppleJuice");
        svc.TryResolve(123, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessDeleteItem_KeepsEntryForLateConsumers()
    {
        // Two independent subscribers read the same stream at different paces. The
        // gift-detection path in Arwen calls TryResolve AFTER the inventory service
        // has already seen the delete — entries must remain queryable to avoid the
        // race that originally dropped calibration observations.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream);
        await RunUntilDrainedAsync(svc, stream);

        svc.TryResolve(42, out var name).Should().BeTrue();
        name.Should().Be("Moonstone");
    }

    [Fact]
    public async Task LiveSubscriber_ReceivesAddAndDeleteInOrder()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream);
        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        await RunUntilDrainedAsync(svc, stream);

        events.Should().HaveCount(2);
        events[0].Kind.Should().Be(InventoryEventKind.Added);
        events[0].InstanceId.Should().Be(42);
        events[0].InternalName.Should().Be("Moonstone");
        events[1].Kind.Should().Be(InventoryEventKind.Deleted);
        events[1].InstanceId.Should().Be(42);
        events[1].InternalName.Should().Be("Moonstone");
    }

    [Fact]
    public async Task IgnoresUnknownProcessDeleteItem()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessDeleteItem(999)");
        var svc = new InventoryService(stream);
        var deleted = new List<InventoryEvent>();
        using var sub = svc.Subscribe(e => { if (e.Kind == InventoryEventKind.Deleted) deleted.Add(e); });

        await RunUntilDrainedAsync(svc, stream);

        deleted.Should().BeEmpty();
        svc.TryResolve(999, out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubscribeAfterAdd_ReplaysCurrentMapAsAddedEvents()
    {
        // The late-subscribe race that caused issue #7: the seed AddItem fires
        // during PlayerLogStream's session-replay flush, before the gated module
        // attaches its handler. Subscribe must replay the live map so the new
        // subscriber sees the same history as one that was attached upfront.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessAddItem(BarleySeeds(7), -1, True)");
        var svc = new InventoryService(stream);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().HaveCount(2);
        replayed.Should().AllSatisfy(e => e.Kind.Should().Be(InventoryEventKind.Added));
        replayed.Select(e => (e.InstanceId, e.InternalName)).Should().BeEquivalentTo(new[]
        {
            (42L, "Moonstone"),
            (7L, "BarleySeeds"),
        });
    }

    [Fact]
    public async Task SubscribeAfterDelete_DoesNotReplayDeletedEntry()
    {
        // Deleted entries are retained for TryResolve, but a brand-new subscriber
        // shouldn't be told an item exists that's already gone. Otherwise Samwise
        // would treat a stale id as a candidate seed for the next plant.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().BeEmpty();
        // TryResolve still returns the name (Arwen's gift-attribution path).
        svc.TryResolve(42, out var name).Should().BeTrue();
        name.Should().Be("Moonstone");
    }

    [Fact]
    public async Task SubscribeReplay_PreservesOriginalTimestamps()
    {
        // Samwise's plant-resolve window is 500 ms off SetPetOwner — replay
        // events with synthetic "now" timestamps would pass the window even
        // for items added an hour ago, breaking the correlation.
        var ts1 = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2026, 4, 25, 10, 0, 1, DateTimeKind.Utc);
        var stream = new ScriptedStream(
            new RawLogLine(ts1, "[10:00:00] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)"),
            new RawLogLine(ts2, "[10:00:01] LocalPlayer: ProcessAddItem(BarleySeeds(7), -1, True)"));
        var svc = new InventoryService(stream);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().HaveCount(2);
        replayed.Single(e => e.InstanceId == 42).Timestamp.Should().Be(ts1);
        replayed.Single(e => e.InstanceId == 7).Timestamp.Should().Be(ts2);
    }

    [Fact]
    public async Task DisposeSubscription_StopsFurtherEvents()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        events.Should().HaveCount(1);

        sub.Dispose();
        sub.Dispose(); // idempotent

        stream.Push("[00:00:02] LocalPlayer: ProcessAddItem(AppleJuice(99), -1, False)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        events.Should().HaveCount(1, "the disposed subscription must not receive further events");

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task SubscribeReplay_CarriesCurrentStackSize()
    {
        // Once a stack has been mutated by UpdateItemCode, a fresh subscriber must
        // see the post-mutation size on the synthesized Added event so the view
        // doesn't render a stale "1" until the next live event.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)",
            "[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 201920, True)"); // size 4
        var svc = new InventoryService(stream);
        await RunUntilDrainedAsync(svc, stream);

        var replayed = new List<InventoryEvent>();
        using var sub = svc.Subscribe(replayed.Add);

        replayed.Should().ContainSingle();
        replayed[0].Kind.Should().Be(InventoryEventKind.Added);
        replayed[0].InstanceId.Should().Be(100);
        replayed[0].StackSize.Should().Be(4);
    }

    [Fact]
    public async Task UpdateItemCode_FiresStackChangedEventWithNewSize()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        stream.Push("[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 201920, True)"); // size 4
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().HaveCount(2);
        events[0].Kind.Should().Be(InventoryEventKind.Added);
        events[0].StackSize.Should().Be(1);
        events[1].Kind.Should().Be(InventoryEventKind.StackChanged);
        events[1].InstanceId.Should().Be(100);
        events[1].InternalName.Should().Be("Guava");
        events[1].StackSize.Should().Be(4);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task UpdateItemCode_NoOpSizeChange_DoesNotFire()
    {
        // A no-op UpdateItemCode (size unchanged) shouldn't push noise to subscribers.
        // In practice the game doesn't repeat the same code, but defending against it
        // keeps the event stream tight.
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        // code 0 → size 1; same as the AddItem default.
        stream.Push("[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 0, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Should().ContainSingle(e => e.Kind == InventoryEventKind.Added);
        events.Should().NotContain(e => e.Kind == InventoryEventKind.StackChanged);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task RemoveFromStorageVault_FiresStackChangedWithLiteralSize()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new InventoryService(stream);
        var runTask = svc.StartAsync(CancellationToken.None);

        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        stream.Push("[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), 116, True)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        stream.Push("[00:00:01] LocalPlayer: ProcessRemoveFromStorageVault(-131, -1, 100, 46)");
        await stream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        events.Where(e => e.Kind == InventoryEventKind.StackChanged)
              .Should().ContainSingle()
              .Which.StackSize.Should().Be(46);

        await svc.StopAsync(CancellationToken.None);
        _ = runTask;
    }

    [Fact]
    public async Task DeleteEvent_CarriesLastKnownStackSize()
    {
        // Arwen's gift-attribution path needs the pre-delete size in the Deleted
        // event (or via TryGetStackSize after) — surface it on the event so
        // event-driven views (Palantir) get parity.
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(GiantSkull(100), -1, True)",
            "[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 3211264, True)", // size 50
            "[00:00:03] LocalPlayer: ProcessDeleteItem(100)");
        var svc = new InventoryService(stream);
        var events = new List<InventoryEvent>();
        using var sub = svc.Subscribe(events.Add);

        await RunUntilDrainedAsync(svc, stream);

        var del = events.Single(e => e.Kind == InventoryEventKind.Deleted);
        del.InstanceId.Should().Be(100);
        del.StackSize.Should().Be(50);
    }

    [Fact]
    public async Task SubscriberException_DoesNotBreakOtherSubscribers()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)");
        var svc = new InventoryService(stream);

        var goodEvents = new List<InventoryEvent>();
        using var bad = svc.Subscribe(_ => throw new InvalidOperationException("boom"));
        using var good = svc.Subscribe(goodEvents.Add);

        await RunUntilDrainedAsync(svc, stream);

        goodEvents.Should().ContainSingle(e => e.InstanceId == 42 && e.Kind == InventoryEventKind.Added);
    }

    private static async Task RunUntilDrainedAsync(InventoryService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private sealed class ScriptedStream : IPlayerLogStream
    {
        private readonly Channel<RawLogLine> _channel = Channel.CreateUnbounded<RawLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public ScriptedStream(params string[] lines) : this(lines.Select(l => new RawLogLine(DateTime.UtcNow, l)).ToArray()) { }

        public ScriptedStream(params RawLogLine[] lines)
        {
            if (lines.Length == 0)
            {
                // No initial lines — leave _drained signalled so callers can use Push.
                _drained.TrySetResult();
                return;
            }
            Interlocked.Add(ref _pending, lines.Length);
            foreach (var line in lines) _channel.Writer.TryWrite(line);
        }

        public void Push(string line)
        {
            // Reset drain latch for the next batch so callers can wait on the
            // newly pushed line(s) without a stale completion firing.
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(new RawLogLine(DateTime.UtcNow, line));
        }

        public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);
        public Task WaitForDrainAsync(TimeSpan timeout) => _drained.Task.WaitAsync(timeout);

        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var line))
                {
                    yield return line;
                    if (Interlocked.Decrement(ref _pending) == 0)
                        _drained.TrySetResult();
                }
            }
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
