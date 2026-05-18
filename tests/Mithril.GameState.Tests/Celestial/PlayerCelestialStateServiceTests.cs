using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Celestial;
using Mithril.GameState.Celestial.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Celestial;

public sealed class PlayerCelestialStateServiceTests
{
    private static readonly DateTime Stamp = new(2026, 5, 18, 19, 50, 42, DateTimeKind.Utc);

    private const string CrescentLine =
        "[19:50:42] LocalPlayer: ProcessSetCelestialInfo(WaxingCrescentMoon)";
    private const string FullLine =
        "[20:30:00] LocalPlayer: ProcessSetCelestialInfo(FullMoon)";

    private static PlayerCelestialStateService NewService(
        ScriptedStream stream, IDiagnosticsSink? diag = null) =>
        new(stream, new CelestialLogParser(), diag);

    [Fact]
    public async Task Cold_start_Current_is_null()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        try
        {
            stream.Push(Stamp, "[19:50:42] LocalPlayer: ProcessAddItem(Barley(1), 0, False)");
            await RunUntilDrainedAsync(svc, stream);
            svc.Current.Should().BeNull();
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Celestial_line_populates_Current_with_phase_and_utc_instant()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        try
        {
            stream.Push(Stamp, CrescentLine);
            await RunUntilDrainedAsync(svc, stream);

            svc.Current.Should().NotBeNull();
            svc.Current!.Phase.Should().Be(MoonPhase.WaxingCrescent);
            svc.Current.RawPhase.Should().Be("WaxingCrescentMoon");
            svc.Current.DisplayName.Should().Be("Waxing Crescent");
            svc.Current.MeasuredAt.Should().Be(new DateTimeOffset(Stamp));
            svc.Current.MeasuredAt.Offset.Should().Be(TimeSpan.Zero);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Latest_phase_wins_on_rollover()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        try
        {
            stream.Push(Stamp, CrescentLine);
            stream.Push(Stamp.AddMinutes(40), FullLine);
            await RunUntilDrainedAsync(svc, stream);

            svc.Current!.Phase.Should().Be(MoonPhase.FullMoon);
            svc.Current.RawPhase.Should().Be("FullMoon");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_after_observed_replays_synchronously_then_delivers_live()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(Stamp, CrescentLine);
            await stream.WaitForDrainAsync(cts.Token);

            var seen = new List<CelestialInfo>();
            using var sub = svc.Subscribe(seen.Add);
            seen.Should().ContainSingle().Which.Phase.Should().Be(MoonPhase.WaxingCrescent);

            stream.Push(Stamp.AddMinutes(40), FullLine);
            await stream.WaitForDrainAsync(cts.Token);

            seen.Should().HaveCount(2);
            seen[1].Phase.Should().Be(MoonPhase.FullMoon);
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
    public async Task Subscribe_with_no_phase_does_not_invoke_handler()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
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

    [Fact]
    public async Task Disposed_subscription_stops_receiving()
    {
        var stream = new ScriptedStream();
        var svc = NewService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            var seen = new List<CelestialInfo>();
            var sub = svc.Subscribe(seen.Add);
            sub.Dispose();

            stream.Push(Stamp, CrescentLine);
            await stream.WaitForDrainAsync(cts.Token);

            seen.Should().BeEmpty();
            svc.Current.Should().NotBeNull("the service still tracks state even with no subscribers");
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
    public async Task Unmapped_token_writes_an_Error_diagnostic_once_per_distinct_token()
    {
        var stream = new ScriptedStream();
        var diag = new DiagnosticsSink();
        var svc = NewService(stream, diag);
        try
        {
            // Same unmapped token replayed 3× (login + roll-overs); a second,
            // different unmapped token once.
            stream.Push(Stamp, "[19:50:42] LocalPlayer: ProcessSetCelestialInfo(BloodMoonEclipse)");
            stream.Push(Stamp.AddMinutes(40), "[20:30:00] LocalPlayer: ProcessSetCelestialInfo(BloodMoonEclipse)");
            stream.Push(Stamp.AddHours(1), "[20:50:00] LocalPlayer: ProcessSetCelestialInfo(BloodMoonEclipse)");
            stream.Push(Stamp.AddHours(2), "[21:50:00] LocalPlayer: ProcessSetCelestialInfo(VoidMoon)");
            await RunUntilDrainedAsync(svc, stream);

            var errors = diag.Snapshot()
                .Where(e => e.Level == DiagnosticLevel.Error
                            && e.Category == "GameState.Celestial")
                .ToArray();

            errors.Should().HaveCount(2, "one Error per distinct unmapped token, deduped on replay");
            errors.Should().ContainSingle(e => e.Message.Contains("'BloodMoonEclipse'"));
            errors.Should().ContainSingle(e => e.Message.Contains("'VoidMoon'"));
            errors[0].Message.Should().Contain("no MoonPhase enum member");

            // The value is still tracked despite being unmapped.
            svc.Current!.Phase.Should().Be(MoonPhase.Unknown);
            svc.Current.RawPhase.Should().Be("VoidMoon");
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Recognised_tokens_emit_no_Error_diagnostic()
    {
        var stream = new ScriptedStream();
        var diag = new DiagnosticsSink();
        var svc = NewService(stream, diag);
        try
        {
            stream.Push(Stamp, CrescentLine);
            stream.Push(Stamp.AddMinutes(40), FullLine);
            await RunUntilDrainedAsync(svc, stream);

            diag.Snapshot().Should().NotContain(e => e.Level == DiagnosticLevel.Error);
        }
        finally { await StopAsync(svc); }
    }

    private static async Task RunUntilDrainedAsync(PlayerCelestialStateService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(PlayerCelestialStateService svc)
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
