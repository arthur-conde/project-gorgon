using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Movement;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Movement;

public sealed class PlayerPositionTrackerTests
{
    private static readonly DateTime Stamp = new(2026, 5, 18, 10, 45, 47, DateTimeKind.Utc);

    private const string PosLine =
        "[10:45:47] LocalPlayer: ProcessNewPosition((834.09, 290.24, 3480.81), (0,0,0,1), Walk, OnLand, UseTeleportationCircle, Looping, 0, False, True, 1, 2)";

    private const string PosLine2 =
        "[11:10:39] LocalPlayer: ProcessNewPosition((-790.06, 309.18, -3386.07), (0,0,0,1), Run, OnLand, Zone, Looping, 0, False, True, 1, 2)";

    private const string SpawnLine =
        "[10:00:00] LocalPlayer: ProcessAddPlayer(1, 2, \"@Base2-m(sex=m;Face=@eq(a=1))\", \"Emraell\", \"A player!\", System.String[], (1522.22, 112.27, 288.13), (0,0,0,1), Idle, Standing, 0, 0, True)";

    private static PlayerPositionTracker NewTracker(ScriptedStream stream, PlayerAreaTracker? area = null) =>
        new(stream, new PlayerPositionParser(), area ?? new PlayerAreaTracker(new AreaTransitionParser()));

    [Fact]
    public async Task Position_line_populates_Current_with_coords_and_utc_instant()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            stream.Push(Stamp, PosLine);
            await RunUntilDrainedAsync(svc, stream);

            svc.Current.Should().NotBeNull();
            svc.Current!.X.Should().Be(834.09);
            svc.Current.Y.Should().Be(290.24);
            svc.Current.Z.Should().Be(3480.81);
            svc.Current.MeasuredAt.Should().Be(new DateTimeOffset(Stamp));
            svc.Current.MeasuredAt.Offset.Should().Be(TimeSpan.Zero);
            svc.Current.Source.Should().Be(PlayerPositionSource.Movement);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Local_ProcessAddPlayer_spawn_line_populates_Current()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            stream.Push(Stamp, SpawnLine);
            await RunUntilDrainedAsync(svc, stream);

            svc.Current.Should().NotBeNull();
            svc.Current!.X.Should().Be(1522.22);
            svc.Current.Z.Should().Be(288.13);
            svc.Current.Source.Should().Be(PlayerPositionSource.Spawn);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Latest_position_wins()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            stream.Push(Stamp, PosLine);
            stream.Push(Stamp.AddMinutes(25), PosLine2);
            await RunUntilDrainedAsync(svc, stream);

            svc.Current!.X.Should().Be(-790.06);
            svc.Current.Z.Should().Be(-3386.07);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Non_position_lines_do_not_affect_Current()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            stream.Push(Stamp, "[10:00:00] LocalPlayer: ProcessAddItem(Barley(1), 0, False)");
            await RunUntilDrainedAsync(svc, stream);
            svc.Current.Should().BeNull();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Also_feeds_shared_area_tracker_from_same_stream()
    {
        var stream = new ScriptedStream();
        var area = new PlayerAreaTracker(new AreaTransitionParser());
        var svc = NewTracker(stream, area);
        try
        {
            stream.Push(Stamp, "[10:30:00] LOADING LEVEL AreaSerbule");
            stream.Push(Stamp, PosLine);
            await RunUntilDrainedAsync(svc, stream);

            area.CurrentArea.Should().Be("AreaSerbule");
            svc.Current.Should().NotBeNull();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_after_observed_replays_synchronously_then_delivers_live()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp, PosLine);
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<PlayerPosition>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle().Which.X.Should().Be(834.09);

            stream.Push(Stamp.AddMinutes(25), PosLine2);
            await stream.WaitForDrainAsync(cts.Token);

            seen.Should().HaveCount(2);
            seen[1].X.Should().Be(-790.06);
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
    public async Task Subscribe_with_no_position_does_not_invoke_handler()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var runTask = svc.StartAsync(cts.Token);

            var seen = 0;
            using var sub = svc.Subscribe(_ => seen++);
            seen.Should().Be(0);

            await cts.CancelAsync();
            _ = runTask;
        }
        finally { await StopAsync(svc); }
    }

    private static async Task RunUntilDrainedAsync(PlayerPositionTracker svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(PlayerPositionTracker svc)
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
