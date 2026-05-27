using System.Diagnostics;
using Arda.Abstractions.Logs;
using Arda.Ingest.Coordinator;
using FluentAssertions;
using Xunit;

namespace Arda.Ingest.Tests;

public class PlayerLogSourceTests : IDisposable
{
    private readonly string _logDir;

    public PlayerLogSourceTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ArdaPlayerLogSourceTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_logDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
            Directory.Delete(_logDir, recursive: true);
    }

    [Fact]
    public async Task Lines_MidSessionRotation_ObservesBothHalves()
    {
        var playerLog = Path.Combine(_logDir, "Player.log");
        var prevLog = Path.Combine(_logDir, "Player-prev.log");

        File.WriteAllText(playerLog, "[10:00:00] First session line A\n[10:00:01] First session line B\n");

        var source = new PlayerLogSource(
            _logDir,
            TimeProvider.System,
            pollInterval: TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var observed = new List<LogLine>();
        var gate = new object();

        var consumer = Task.Run(async () =>
        {
            await foreach (var line in source.Lines(cts.Token))
            {
                lock (gate) observed.Add(line);
            }
        }, cts.Token);

        await WaitFor(() => Snapshot(observed, gate).Any(l => l.Log == "First session line B"), cts.Token);

        // Simulate PG's mid-session rotation: move Player.log → Player-prev.log,
        // then create a fresh Player.log with the post-restart banner burst.
        File.Move(playerLog, prevLog, overwrite: true);
        Thread.Sleep(50); // ensure new file gets a distinct creation time
        File.WriteAllText(playerLog, "[10:05:00] Post-rotation banner\n[10:05:01] Post-rotation line\n");
        // Defensively bump creation time in case the filesystem reuses it.
        File.SetCreationTimeUtc(playerLog, DateTime.UtcNow.AddSeconds(1));

        await WaitFor(() => Snapshot(observed, gate).Any(l => l.Log == "Post-rotation line"), cts.Token);

        cts.Cancel();
        try { await consumer; } catch (OperationCanceledException) { }

        var snapshot = Snapshot(observed, gate);
        snapshot.Select(o => o.Log).Should().Contain("First session line A");
        snapshot.Select(o => o.Log).Should().Contain("First session line B");
        snapshot.Select(o => o.Log).Should().Contain("Post-rotation banner");
        snapshot.Select(o => o.Log).Should().Contain("Post-rotation line");

        // Pre-rotation lines were seen live (replay=false after catch-up);
        // post-rotation lines start as replay (we re-anchor the fresh file).
        snapshot.Where(l => l.Log == "First session line A").Should().ContainSingle(
            "the old tailer's offset is carried into prev — no duplicate yield of already-read lines");
    }

    private static List<LogLine> Snapshot(List<LogLine> observed, object gate)
    {
        lock (gate) return [.. observed];
    }

    private static async Task WaitFor(Func<bool> predicate, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Condition not met within 5s");
            await Task.Delay(20, ct);
        }
    }
}
