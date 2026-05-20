using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Inventory;
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
        stream.Push("[20:48:30] LocalPlayer: ProcessAddItem(BarleySeeds(86940428), -1, False)",
            new DateTime(2026, 4, 15, 20, 48, 30, DateTimeKind.Utc));
        await stream.WaitForDrainAsync(cts.Token);

        // 2) ProcessUpdateItemCode — fires StackChanged on the existing entry.
        //    Pre-fix this triggered InvalidOperationException in OnInventoryEvent.
        stream.Push("[20:50:22] LocalPlayer: ProcessUpdateItemCode(86940428, 796683, True)",
            new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc));
        await stream.WaitForDrainAsync(cts.Token);

        observed.Should().BeNull(
            "a new InventoryEventKind must not crash a consumer that handles only Added/Deleted");
        addedCount.Should().Be(1, "exactly one Added event from ProcessAddItem");
        stackChangedCount.Should().Be(1, "ProcessUpdateItemCode must fire StackChanged on the existing entry");

        await cts.CancelAsync();
        try { await inv.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    /// <summary>
    /// Minimal <see cref="ILogStreamDriver"/> fake for Samwise tests after the
    /// L1 migration (#565): only the <see cref="LocalPlayerLogLine"/> pipe is
    /// exercised here (no chat). Strips the standard
    /// <c>[HH:MM:SS] LocalPlayer: </c> envelope from pushed Player.log shapes
    /// so the test reads like the pre-L1 raw-line stream did.
    /// </summary>
    private sealed class ScriptedStream : ILogStreamDriver
    {
        private const int TsPrefixLen = 11;             // length of "[HH:MM:SS] "
        private const string ActorToken = "LocalPlayer: ";

        private readonly Channel<LocalPlayerLogLine> _channel =
            Channel.CreateUnbounded<LocalPlayerLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public ILogSubscription Subscribe<T>(
            Func<LogEnvelope<T>, ValueTask> handler,
            LogSubscriptionOptions? options = null) where T : class
        {
            if (typeof(T) == typeof(LocalPlayerLogLine))
            {
                var typed = (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler;
                var cts = new CancellationTokenSource();
                _ = Task.Run(() => PumpAsync(typed, cts.Token));
                // Fire-and-forget cancel on dispose. Any in-flight handler invocation
                // unwinds on its own thread; tests don't need to wait for it.
                return new Sub(() => { try { cts.Cancel(); } catch { } });
            }
            // Chat / other pipes: accept-and-ignore so InventoryService's second
            // Subscribe<RawLogLine> call doesn't blow up. Samwise tests don't
            // exercise the chat path.
            return new Sub(() => { });
        }

        public void Push(string line, DateTime? timestamp = null)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(new LocalPlayerLogLine(
                new DateTimeOffset(timestamp ?? DateTime.UtcNow, TimeSpan.Zero),
                StripEnvelope(line), 0, 0));
        }

        public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);

        private async Task PumpAsync(
            Func<LogEnvelope<LocalPlayerLogLine>, ValueTask> handler, CancellationToken ct)
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out var line))
                    {
                        try { await handler(new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: false)).ConfigureAwait(false); }
                        catch { /* mirror driver containment */ }
                        finally
                        {
                            if (Interlocked.Decrement(ref _pending) == 0) _drained.TrySetResult();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on dispose */ }
        }

        private static string StripEnvelope(string line)
        {
            var idx = 0;
            if (line.Length > TsPrefixLen
                && line[0] == '[' && line[3] == ':' && line[6] == ':' && line[9] == ']')
                idx = TsPrefixLen;
            if (idx + ActorToken.Length <= line.Length
                && line.IndexOf(ActorToken, idx, StringComparison.Ordinal) == idx)
                idx += ActorToken.Length;
            return idx == 0 ? line : line.Substring(idx);
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class Sub : ILogSubscription
        {
            private readonly Action _onDispose;
            public Sub(Action onDispose) { _onDispose = onDispose; }
            public string Id => "samwise#scripted";
            public LogSubscriptionDiagnostics Diagnostics =>
                new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
            public event EventHandler? StateChanged { add { } remove { } }
            public void Dispose() => _onDispose();
        }
    }
}
