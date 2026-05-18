using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Pins;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Pins;

public sealed class PlayerPinTrackerTests
{
    private static readonly DateTime Stamp = new(2026, 5, 18, 10, 10, 3, DateTimeKind.Utc);

    private static PlayerPinTracker NewTracker(ScriptedStream stream, PlayerAreaTracker? area = null) =>
        new(stream, new MapPinParser(),
            area ?? new PlayerAreaTracker(new AreaTransitionParser()));

    private static string Area(string key) => $"[10:00:00] LOADING LEVEL {key}";
    private static string Add(double x, double z, string label, int b = 0, int c = 0) =>
        $"[10:10:03] LocalPlayer: ProcessMapPinAdd(1, {b}, {c}, ({x:0.00}, 0.00, {z:0.00}), \"{label}\")";
    private static string Remove(double x, double z, string label) =>
        $"[10:10:03] LocalPlayer: ProcessMapPinRemove(1, 0, 0, ({x:0.00}, 0.00, {z:0.00}), \"{label}\")";

    [Fact]
    public async Task Add_populates_current_area_pins_with_decoded_appearance()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Add(1425.06, 2924.99, "South", b: 1, c: 1));
            await RunUntilDrainedAsync(svc, stream);

            svc.CurrentArea.Should().Be("AreaSerbule");
            var pin = svc.CurrentAreaPins.Should().ContainSingle().Subject;
            pin.Label.Should().Be("South");
            pin.X.Should().Be(1425.06);
            pin.Z.Should().Be(2924.99);
            pin.Shape.Should().Be(PinShape.Square);
            pin.Color.Should().Be(PinColor.Red);
            pin.Appearance.Should().Be("red square");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Replay_burst_is_idempotent_no_duplicates_no_extra_events()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            var seen = new List<PinSetChanged>();
            using var sub = svc.Subscribe(seen.Add);

            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Add(10, 20, "A"));
            stream.Push(Stamp, Add(30, 40, "B"));
            await stream.WaitForDrainAsync(cts.Token);
            // Login/area-entry re-emits the whole set verbatim.
            stream.Push(Stamp, Add(10, 20, "A"));
            stream.Push(Stamp, Add(30, 40, "B"));
            await stream.WaitForDrainAsync(cts.Token);

            svc.CurrentAreaPins.Should().HaveCount(2);
            seen.Count(n => n.Kind == PinSetChange.Added).Should().Be(2);
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            _ = runTask;
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Remove_deletes_by_coordinate()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Add(10, 20, "A"));
            stream.Push(Stamp, Remove(10, 20, "A"));
            await RunUntilDrainedAsync(svc, stream);

            svc.CurrentAreaPins.Should().BeEmpty();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Rename_is_remove_then_add_at_same_coord()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Add(10, 20, ""));
            stream.Push(Stamp, Remove(10, 20, ""));
            stream.Push(Stamp, Add(10, 20, "Calib 1"));
            await RunUntilDrainedAsync(svc, stream);

            svc.CurrentAreaPins.Should().ContainSingle()
                .Which.Label.Should().Be("Calib 1");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Area_change_swaps_the_set_and_raises_AreaChanged()
    {
        var stream = new ScriptedStream();
        var area = new PlayerAreaTracker(new AreaTransitionParser());
        var svc = NewTracker(stream, area);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Add(10, 20, "A"));
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<PinSetChanged>();
            using var sub = svc.Subscribe(seen.Add); // Snapshot replay first
            stream.Push(Stamp, Area("AreaEltibule"));
            await stream.WaitForDrainAsync(cts.Token);

            svc.CurrentArea.Should().Be("AreaEltibule");
            svc.CurrentAreaPins.Should().BeEmpty();
            seen.Should().Contain(n => n.Kind == PinSetChange.AreaChanged && n.Area == "AreaEltibule");
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            _ = runTask;
            svc.Dispose();
        }
    }

    [Fact]
    public async Task Subscribe_replays_snapshot_synchronously_then_delivers_live()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Add(10, 20, "A"));
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<PinSetChanged>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle()
                .Which.Should().Match<PinSetChanged>(n =>
                    n.Kind == PinSetChange.Snapshot && n.Pins.Count == 1);

            stream.Push(Stamp, Add(30, 40, "B"));
            await stream.WaitForDrainAsync(cts.Token);

            var added = seen.Last();
            added.Kind.Should().Be(PinSetChange.Added);
            added.Pin!.Label.Should().Be("B");
            added.ObservedAt.Offset.Should().Be(TimeSpan.Zero);
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            _ = runTask;
            svc.Dispose();
        }
    }

    private static async Task RunUntilDrainedAsync(PlayerPinTracker svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(PlayerPinTracker svc)
    {
        try { await svc.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { /* test cleanup */ }
        svc.Dispose();
    }

    private sealed class ScriptedStream : IPlayerLogStream
    {
        private readonly Channel<RawLogLine> _channel = Channel.CreateUnbounded<RawLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public ScriptedStream() => _drained.TrySetResult();

        public void Push(DateTime ts, string line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(new RawLogLine(ts, line));
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

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
