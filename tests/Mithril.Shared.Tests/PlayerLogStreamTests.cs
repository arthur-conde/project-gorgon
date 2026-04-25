using System.IO;
using FluentAssertions;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests;

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class PlayerLogStreamTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;
    private readonly GameConfig _config;

    public PlayerLogStreamTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-playerlog");
        _logPath = Path.Combine(_dir, "Player.log");
        _config = new GameConfig
        {
            GameRoot = _dir,
            PollIntervalSeconds = 0.25,
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task LateSubscriber_ReceivesEveryReplayedLine_EvenWhenBufferExceedsChannelCapacity()
    {
        // Simulates the real-world failure mode: a mid-session Mithril launch
        // finds a session-start ~50K lines back. A late subscriber (gate-delayed
        // ingestion service) used to lose the oldest replay lines to the bounded
        // channel's DropOldest. After the fix, replay is yielded directly from
        // memory and every line is delivered.
        const int LineCount = 2500; // well above the 1024 channel bound
        using (var writer = new StreamWriter(_logPath, append: false))
        {
            await writer.WriteLineAsync("LocalPlayer: ProcessAddPlayer(1, 2, \"desc\", \"TestChar\")");
            for (var i = 0; i < LineCount; i++)
                await writer.WriteLineAsync($"[00:00:00] line-{i:D5}");
        }

        using var stream = new PlayerLogStream(_config);

        // Hold a primer subscription open (drain-and-discard) so RunAsync stays
        // running and the session-replay buffer persists for the late joiner.
        // Without it, StopRunning fires once _subs goes to zero and clears the
        // buffer.
        using var primerCts = new CancellationTokenSource();
        var primerTask = Task.Run(async () =>
        {
            try { await foreach (var _ in stream.SubscribeAsync(primerCts.Token)) { } }
            catch (OperationCanceledException) { }
        });

        // Let RunAsync perform the initial flush + populate the replay buffer.
        await Task.Delay(750);

        // Late joiner — should get the full session via the yield-replay path,
        // even though the bounded channel could only hold 1024 of these.
        using var lateCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        try
        {
            await foreach (var raw in stream.SubscribeAsync(lateCts.Token))
            {
                received.Add(raw.Line);
                if (received.Count >= LineCount + 1) break;
            }
        }
        finally
        {
            await primerCts.CancelAsync();
            try { await primerTask; } catch { }
        }

        received.Should().HaveCountGreaterThanOrEqualTo(LineCount + 1);
        received[0].Should().Contain("ProcessAddPlayer");
        received.Should().Contain(l => l.Contains("line-00000"));
        received.Should().Contain(l => l.Contains($"line-{LineCount - 1:D5}"));
    }
}
