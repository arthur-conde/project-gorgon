using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Sessions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.GameState.Tests.Sessions;

public sealed class GameSessionServiceTests
{
    private const string BannerEmraell =
        "[12:25:04] Logged in as character Emraell. Time UTC=05/11/2026 12:25:04. Timezone Offset 01:00:00";

    private const string BannerSecond =
        "[14:00:00] Logged in as character Emraell. Time UTC=05/11/2026 14:00:00. Timezone Offset 01:00:00";

    private const string BannerOtherCharacter =
        "[14:00:00] Logged in as character Frodo. Time UTC=05/11/2026 14:00:00. Timezone Offset 01:00:00";

    [Fact]
    public async Task First_banner_populates_Current_and_raises_SessionStarted_and_pushes_to_anchor()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var anchor = new SessionAnchor();
        var svc = new GameSessionService(stream, anchor);
        try
        {
            GameSession? captured = null;
            svc.SessionStarted += (_, s) => captured = s;

            stream.Push(BannerEmraell);
            await RunUntilDrainedAsync(svc, stream);

            svc.Current.Should().NotBeNull();
            svc.Current!.CharacterName.Should().Be("Emraell");
            // Shared anchor was pushed-to so the sequencer (Mithril.Shared) can
            // re-anchor without depending on GameSessionService directly.
            anchor.LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
            captured.Should().NotBeNull();
            captured!.SessionId.Should().Be(svc.Current.SessionId);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Replaying_same_banner_does_not_re_fire()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var anchor = new SessionAnchor();
        var svc = new GameSessionService(stream, anchor);
        try
        {
            var startedCount = 0;
            var anchorChangedCount = 0;
            svc.SessionStarted += (_, _) => startedCount++;
            anchor.AnchorChanged += (_, _) => anchorChangedCount++;

            stream.Push(BannerEmraell);
            stream.Push(BannerEmraell);
            stream.Push(BannerEmraell);
            await RunUntilDrainedAsync(svc, stream);

            startedCount.Should().Be(1);
            anchorChangedCount.Should().Be(1);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Second_banner_with_new_login_mints_new_session()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new GameSessionService(stream);
        try
        {
            var sessions = new List<GameSession>();
            svc.SessionStarted += (_, s) => sessions.Add(s);

            stream.Push(BannerEmraell);
            stream.Push(BannerSecond);
            await RunUntilDrainedAsync(svc, stream);

            sessions.Should().HaveCount(2);
            sessions[0].LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
            sessions[1].LoggedInUtc.Should().Be(new DateTime(2026, 5, 11, 14, 0, 0, DateTimeKind.Utc));
            sessions[1].SessionId.Should().NotBe(sessions[0].SessionId);
            svc.Current!.SessionId.Should().Be(sessions[1].SessionId);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Different_character_mints_new_session_id()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new GameSessionService(stream);
        try
        {
            var sessions = new List<GameSession>();
            svc.SessionStarted += (_, s) => sessions.Add(s);

            stream.Push(BannerEmraell);
            stream.Push(BannerOtherCharacter);
            await RunUntilDrainedAsync(svc, stream);

            sessions.Should().HaveCount(2);
            sessions[0].CharacterName.Should().Be("Emraell");
            sessions[1].CharacterName.Should().Be("Frodo");
            sessions[1].SessionId.Should().NotBe(sessions[0].SessionId);
        }
        finally { await StopAsync(svc); }
    }

    [Fact]
    public async Task Subscribe_after_session_observed_replays_current_synchronously()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new GameSessionService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = svc.StartAsync(cts.Token);
        try
        {
            stream.Push(BannerEmraell);
            await stream.WaitForDrainAsync(cts.Token);

            var replayed = new List<GameSession>();
            using var sub = svc.Subscribe(replayed.Add);

            replayed.Should().HaveCount(1);
            replayed[0].SessionId.Should().Be(svc.Current!.SessionId);

            // Live event still arrives without re-subscribing — the service is
            // still running and a second banner mints + delivers a new session.
            stream.Push(BannerSecond);
            await stream.WaitForDrainAsync(cts.Token);

            replayed.Should().HaveCount(2);
            replayed[1].SessionId.Should().NotBe(replayed[0].SessionId);
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
    public async Task Subscribe_with_no_current_session_does_not_invoke_handler()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new GameSessionService(stream);
        try
        {
            // Service started but no banner yet.
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
    public async Task Non_banner_lines_do_not_affect_session()
    {
        var stream = new ScriptedStream(Array.Empty<string>());
        var svc = new GameSessionService(stream);
        try
        {
            var starts = 0;
            svc.SessionStarted += (_, _) => starts++;

            stream.Push("[12:25:00] LocalPlayer: ProcessAddPlayer(1, 2, \"\", \"Emraell\")");
            stream.Push("[12:25:01] Some unrelated engine log line.");
            stream.Push(BannerEmraell);
            stream.Push("[12:25:05] LocalPlayer: ProcessAddItem(Barley(1), 0, False)");
            await RunUntilDrainedAsync(svc, stream);

            starts.Should().Be(1);
            svc.Current!.CharacterName.Should().Be("Emraell");
        }
        finally { await StopAsync(svc); }
    }

    private static async Task RunUntilDrainedAsync(GameSessionService svc, ScriptedStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    private static async Task StopAsync(GameSessionService svc)
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

        public ScriptedStream(params string[] lines)
        {
            if (lines.Length == 0)
            {
                _drained.TrySetResult();
                return;
            }
            Interlocked.Add(ref _pending, lines.Length);
            foreach (var line in lines) _channel.Writer.TryWrite(new RawLogLine(DateTime.UtcNow, line));
        }

        public void Push(string line)
        {
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
