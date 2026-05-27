using System.Diagnostics;
using Arda.Abstractions.Logs;
using Arda.Ingest.Coordinator;
using FluentAssertions;
using Xunit;

namespace Arda.Ingest.Tests;

public class ChatLogSourceTests : IDisposable
{
    private readonly string _chatDir;

    public ChatLogSourceTests()
    {
        _chatDir = Path.Combine(Path.GetTempPath(), "ArdaChatLogSourceTests-" + Guid.NewGuid());
        Directory.CreateDirectory(_chatDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_chatDir))
            Directory.Delete(_chatDir, recursive: true);
    }

    [Fact]
    public async Task Lines_LateFlushOnRollover_DrainedBeforeSwitch()
    {
        var dayOne = Path.Combine(_chatDir, "Chat-26-01-15.log");
        var dayTwo = Path.Combine(_chatDir, "Chat-26-01-16.log");

        // Include a banner line so ChatSessionStartScanner anchors here rather
        // than seeking to EOF (skipping the test fixture).
        File.WriteAllText(dayOne,
            "26-01-15 23:59:49\t* Logged In As Tester. Timezone Offset 0:00:00.\n" +
            "26-01-15 23:59:50\t[Global] hello\n");

        var source = new ChatLogSource(
            _chatDir,
            TimeProvider.System,
            pollInterval: TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var observed = new List<LogLine>();
        var gate = new object();

        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in source.Lines(cts.Token))
                {
                    lock (gate) observed.Add(line);
                }
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await WaitFor(() => Snapshot(observed, gate).Any(l => l.Log.Contains("hello")), cts.Token);

        // Day-2 file appears; PG then flushes one final line to day-1.
        File.WriteAllText(dayTwo, "26-01-16 00:00:05\t[Global] new day\n");
        File.AppendAllText(dayOne, "26-01-15 23:59:59\t[Global] late flush\n");

        await WaitFor(() => Snapshot(observed, gate).Any(l => l.Log.Contains("new day")), cts.Token);

        cts.Cancel();
        try { await consumer; } catch (OperationCanceledException) { }

        var snapshot = Snapshot(observed, gate);
        snapshot.Select(o => o.Log).Should().ContainMatch("*hello*");
        snapshot.Select(o => o.Log).Should().ContainMatch("*late flush*",
            "the final flush to the prior-day file must be drained before switching tailers");
        snapshot.Select(o => o.Log).Should().ContainMatch("*new day*");
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
