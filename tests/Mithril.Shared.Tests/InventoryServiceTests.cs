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
    public async Task FiresItemDeletedEvent_WithResolvedName()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Moonstone(42), -1, True)",
            "[00:00:02] LocalPlayer: ProcessDeleteItem(42)");
        var svc = new InventoryService(stream);
        var deleted = new List<InventoryItem>();
        svc.ItemDeleted += (_, item) => deleted.Add(item);

        await RunUntilDrainedAsync(svc, stream);

        deleted.Should().ContainSingle();
        deleted[0].InstanceId.Should().Be(42);
        deleted[0].InternalName.Should().Be("Moonstone");
    }

    [Fact]
    public async Task IgnoresUnknownProcessDeleteItem()
    {
        var stream = new ScriptedStream(
            "[00:00:01] LocalPlayer: ProcessDeleteItem(999)");
        var svc = new InventoryService(stream);
        var deleted = new List<InventoryItem>();
        svc.ItemDeleted += (_, item) => deleted.Add(item);

        await RunUntilDrainedAsync(svc, stream);

        deleted.Should().BeEmpty();
        svc.TryResolve(999, out _).Should().BeFalse();
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
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pending;

        public ScriptedStream(params string[] lines)
        {
            _pending = lines.Length;
            foreach (var line in lines) _channel.Writer.TryWrite(new RawLogLine(DateTime.UtcNow, line));
        }

        public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);

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
    }
}
