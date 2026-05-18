using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Weather;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Weather;

public sealed class PlayerWeatherTrackerTests
{
    private static readonly DateTime Stamp = new(2026, 5, 18, 19, 50, 42, DateTimeKind.Utc);

    private static PlayerWeatherTracker NewTracker(ScriptedStream stream, PlayerAreaTracker? area = null) =>
        new(stream, new WeatherLogParser(),
            area ?? new PlayerAreaTracker(new AreaTransitionParser()));

    private static string Area(string key) => $"[10:00:00] LOADING LEVEL {key}";
    private static string Set(string condition, bool flag = true) =>
        $"[19:50:42] LocalPlayer: ProcessSetWeather(\"{condition}\", {(flag ? "True" : "False")})";

    [Fact]
    public async Task First_weather_line_populates_Current_for_the_map()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Set("Foggy"));
            await RunUntilDrainedAsync(svc, stream);

            svc.CurrentArea.Should().Be("AreaSerbule");
            svc.Current.Should().NotBeNull();
            svc.Current!.Condition.Should().Be("Foggy");
            svc.Current.Flag.Should().BeTrue();
            svc.Current.MeasuredAt.Offset.Should().Be(TimeSpan.Zero);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Last_writer_wins_on_a_genuine_change()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Set("Foggy"));
            stream.Push(Stamp, Set("Clear", flag: false));
            await RunUntilDrainedAsync(svc, stream);

            svc.Current!.Condition.Should().Be("Clear");
            svc.Current.Flag.Should().BeFalse();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Map_change_drops_weather_until_the_new_map_reports()
    {
        var stream = new ScriptedStream();
        var area = new PlayerAreaTracker(new AreaTransitionParser());
        var svc = NewTracker(stream, area);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Set("Foggy"));
            await stream.WaitForDrainAsync(cts.Token);
            svc.Current!.Condition.Should().Be("Foggy");

            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add); // Snapshot replay first

            // Leave the foggy map: weather must not bleed into the next map.
            stream.Push(Stamp, Area("AreaEltibule"));
            await stream.WaitForDrainAsync(cts.Token);

            svc.CurrentArea.Should().Be("AreaEltibule");
            svc.Current.Should().BeNull(); // unknown — NOT still Foggy
            seen.Should().Contain(n =>
                n.Kind == WeatherChangeKind.AreaChanged
                && n.Area == "AreaEltibule"
                && n.State == null);

            // New map reports its own weather.
            stream.Push(Stamp, Set("Clear", flag: false));
            await stream.WaitForDrainAsync(cts.Token);

            svc.Current!.Condition.Should().Be("Clear");
            seen.Last().Should().Match<WeatherChanged>(n =>
                n.Kind == WeatherChangeKind.Changed
                && n.Area == "AreaEltibule"
                && n.State!.Condition == "Clear");
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
    public async Task Replay_of_unchanged_weather_is_idempotent_no_extra_events()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            stream.Push(Stamp, Set("Foggy"));
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add); // Snapshot
            seen.Should().ContainSingle()
                .Which.Should().Match<WeatherChanged>(n =>
                    n.Kind == WeatherChangeKind.Snapshot && n.State!.Condition == "Foggy");

            // Zone re-emits the current weather verbatim.
            stream.Push(Stamp, Set("Foggy"));
            stream.Push(Stamp, Set("Foggy"));
            await stream.WaitForDrainAsync(cts.Token);

            seen.Should().ContainSingle(); // no further notifications
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
            stream.Push(Stamp, Set("Foggy"));
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<WeatherChanged>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle()
                .Which.Should().Match<WeatherChanged>(n =>
                    n.Kind == WeatherChangeKind.Snapshot && n.State!.Condition == "Foggy");

            stream.Push(Stamp, Set("Rainy"));
            await stream.WaitForDrainAsync(cts.Token);

            var last = seen.Last();
            last.Kind.Should().Be(WeatherChangeKind.Changed);
            last.State!.Condition.Should().Be("Rainy");
            last.ObservedAt.Offset.Should().Be(TimeSpan.Zero);
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
    public async Task Disposed_subscription_stops_receiving_but_state_advances()
    {
        var stream = new ScriptedStream();
        var svc = NewTracker(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp, Area("AreaSerbule"));
            var seen = new List<WeatherChanged>();
            var sub = svc.Subscribe(seen.Add);
            stream.Push(Stamp, Set("Foggy"));
            await stream.WaitForDrainAsync(cts.Token);
            var countAtDispose = seen.Count;
            sub.Dispose();

            stream.Push(Stamp, Set("Rainy"));
            await stream.WaitForDrainAsync(cts.Token);

            seen.Should().HaveCount(countAtDispose); // no further delivery
            svc.Current!.Condition.Should().Be("Rainy"); // state still advances
        }
        finally
        {
            await cts.CancelAsync();
            try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            _ = runTask;
            svc.Dispose();
        }
    }

    private static async Task RunUntilDrainedAsync(PlayerWeatherTracker svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(PlayerWeatherTracker svc)
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
