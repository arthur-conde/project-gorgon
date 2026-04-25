using System.Threading.Channels;
using FluentAssertions;
using Mithril.Shared.Inventory;
using Mithril.Shared.Logging;
using Samwise.Parsing;
using Xunit;

namespace Samwise.Tests;

/// <summary>
/// Pin: Samwise's inventory-event consumer must not throw on
/// <see cref="InventoryEventKind"/> values it doesn't translate.
/// <c>InventoryService</c> fires <see cref="InventoryEventKind.StackChanged"/>
/// on every <c>ProcessUpdateItemCode</c>, <c>ProcessRemoveFromStorageVault</c>,
/// and chat back-fill — Samwise's <c>OnInventoryEvent</c> is shaped to map
/// only Added/Deleted into <see cref="GardenEvent"/>, so unknown kinds must
/// silently skip rather than crash the BackgroundService.
/// </summary>
public class StackChangedResilienceTest
{
    [Fact]
    public async Task UpdateItemCode_FiresStackChanged_SamwiseConsumerSkipsHarmlessly()
    {
        var stream = new ScriptedStream();
        var inv = new InventoryService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = inv.StartAsync(cts.Token);

        var addedCount = 0;
        var stackChangedCount = 0;
        Exception? observed = null;

        // Mirrors GardenIngestionService.OnInventoryEvent — keep in sync. The
        // contract being pinned is "unknown kinds map to null and are skipped",
        // so future kinds added to InventoryService can land without taking
        // Samwise down.
        using var sub = inv.Subscribe(evt =>
        {
            try
            {
                var idStr = evt.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                GardenEvent? ge = evt.Kind switch
                {
                    InventoryEventKind.Added => new AddItem(evt.Timestamp, idStr, evt.InternalName),
                    InventoryEventKind.Deleted => new DeleteItem(evt.Timestamp, idStr),
                    _ => null,
                };
                if (evt.Kind == InventoryEventKind.Added) addedCount++;
                else if (evt.Kind == InventoryEventKind.StackChanged) stackChangedCount++;
                _ = ge;
            }
            catch (Exception ex) { observed = ex; }
        });

        // 1) ProcessAddItem — fires Added, creates the entry.
        stream.Push(new RawLogLine(new DateTime(2026, 4, 15, 20, 48, 30, DateTimeKind.Utc),
            "[20:48:30] LocalPlayer: ProcessAddItem(BarleySeeds(86940428), -1, False)"));
        await stream.WaitForDrainAsync(cts.Token);

        // 2) ProcessUpdateItemCode — fires StackChanged on the existing entry.
        //    Pre-fix this triggered InvalidOperationException in OnInventoryEvent.
        stream.Push(new RawLogLine(new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc),
            "[20:50:22] LocalPlayer: ProcessUpdateItemCode(86940428, 796683, True)"));
        await stream.WaitForDrainAsync(cts.Token);

        observed.Should().BeNull(
            "a new InventoryEventKind must not crash a consumer that handles only Added/Deleted");
        addedCount.Should().Be(1, "exactly one Added event from ProcessAddItem");
        stackChangedCount.Should().Be(1, "ProcessUpdateItemCode must fire StackChanged on the existing entry");

        await cts.CancelAsync();
        try { await inv.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private sealed class ScriptedStream : IPlayerLogStream
    {
        private readonly Channel<RawLogLine> _channel = Channel.CreateUnbounded<RawLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public void Push(RawLogLine line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(line);
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
                    if (Interlocked.Decrement(ref _pending) == 0) _drained.TrySetResult();
                }
            }
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
