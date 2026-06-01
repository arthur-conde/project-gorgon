using System.Diagnostics;
using Arda.Abstractions.Diagnostics;
using Arda.Ingest.Coordinator;
using FluentAssertions;
using Xunit;

namespace Arda.Ingest.Tests;

/// <summary>
/// Verifies that the live-tail poll loop in <see cref="PlayerLogSource"/> /
/// <see cref="ChatLogSource"/> records a pulse on every iteration including
/// empty reads — the load-bearing behaviour for issue #856 (drift defined as
/// "tailer-poll age", not "domain-event arrival age").
/// </summary>
public sealed class IngestPulseTests : IDisposable
{
    private readonly string _logDir;

    public IngestPulseTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ArdaIngestPulseTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_logDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
        {
            try { Directory.Delete(_logDir, recursive: true); }
            catch { /* test cleanup best-effort */ }
        }
    }

    private sealed class RecordingSink : IIngestPulseSink
    {
        private readonly object _gate = new();
        private readonly List<(LogFamily Family, DateTimeOffset At, int Bytes, int Lines)> _entries = [];

        public IReadOnlyList<(LogFamily Family, DateTimeOffset At, int Bytes, int Lines)> Snapshot()
        {
            lock (_gate) return [.. _entries];
        }

        public int CountFor(LogFamily family)
        {
            lock (_gate) return _entries.Count(e => e.Family == family);
        }

        public int EmptyPollsFor(LogFamily family)
        {
            lock (_gate) return _entries.Count(e => e.Family == family && e.Lines == 0);
        }

        public void RecordPoll(LogFamily family, DateTimeOffset polledAt, int bytesRead, int linesEmitted)
        {
            lock (_gate) _entries.Add((family, polledAt, bytesRead, linesEmitted));
        }
    }

    [Fact]
    public async Task PlayerLogSource_PulsesOnEveryLiveTailPoll_IncludingEmptyReads()
    {
        var playerLog = Path.Combine(_logDir, "Player.log");
        File.WriteAllText(playerLog, "[10:00:00] Initial line\n");

        var sink = new RecordingSink();
        var source = new PlayerLogSource(
            _logDir,
            TimeProvider.System,
            pollInterval: TimeSpan.FromMilliseconds(20),
            logger: null,
            pulseSink: sink);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in source.Lines(cts.Token)) { /* drain */ }
        }, cts.Token);

        // Wait long enough for several empty polls past the initial drain.
        await WaitFor(() => sink.EmptyPollsFor(LogFamily.Player) >= 3, cts.Token);

        cts.Cancel();
        try { await consumer; } catch (OperationCanceledException) { }

        sink.CountFor(LogFamily.Player).Should().BeGreaterThanOrEqualTo(3,
            "the live-tail poll loop pulses every iteration even on empty reads");
        sink.EmptyPollsFor(LogFamily.Player).Should().BeGreaterThan(0,
            "drift must be measurable when the file is quiet");
    }

    [Fact]
    public async Task ChatLogSource_DoesNotPulse_WhenNoChatFileExists()
    {
        // No chat file created — source sits in the file-resolution wait loop.
        var sink = new RecordingSink();
        var source = new ChatLogSource(
            _logDir,
            TimeProvider.System,
            pollInterval: TimeSpan.FromMilliseconds(20),
            logger: null,
            pulseSink: sink);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in source.Lines(cts.Token)) { /* drain */ }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        await Task.Delay(250, CancellationToken.None);
        cts.Cancel();
        try { await consumer; } catch (OperationCanceledException) { }

        sink.CountFor(LogFamily.Chat).Should().Be(0,
            "design lock: do not pulse before a file even exists — that's not 'tailer healthy', " +
            "that's 'waiting for setup'");
    }

    [Fact]
    public async Task ChatLogSource_PulsesOnLiveTailPoll_OnceFileExists()
    {
        var chatFile = Path.Combine(_logDir, $"Chat-{DateTime.UtcNow:yy-MM-dd}.log");
        File.WriteAllText(chatFile, "[10:00:00] Initial\n");

        var sink = new RecordingSink();
        var source = new ChatLogSource(
            _logDir,
            TimeProvider.System,
            pollInterval: TimeSpan.FromMilliseconds(20),
            logger: null,
            pulseSink: sink);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in source.Lines(cts.Token)) { /* drain */ }
        }, cts.Token);

        await WaitFor(() => sink.EmptyPollsFor(LogFamily.Chat) >= 3, cts.Token);

        cts.Cancel();
        try { await consumer; } catch (OperationCanceledException) { }

        sink.CountFor(LogFamily.Chat).Should().BeGreaterThanOrEqualTo(3);
        sink.EmptyPollsFor(LogFamily.Chat).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PlayerLogSource_WorksWithNullSink()
    {
        // Pulse sink is optional — older shells and bare unit tests must still work.
        var playerLog = Path.Combine(_logDir, "Player.log");
        File.WriteAllText(playerLog, "[10:00:00] Hello\n");

        var source = new PlayerLogSource(
            _logDir,
            TimeProvider.System,
            pollInterval: TimeSpan.FromMilliseconds(20),
            logger: null,
            pulseSink: null);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in source.Lines(cts.Token)) { /* drain */ }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        await consumer; // no exception
    }

    private static async Task WaitFor(Func<bool> predicate, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(4))
                throw new TimeoutException("Condition not met within 4s");
            await Task.Delay(20, ct);
        }
    }
}
