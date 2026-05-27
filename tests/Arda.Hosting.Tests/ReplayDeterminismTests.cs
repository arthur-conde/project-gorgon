using System.Runtime.CompilerServices;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Hosting.Tests;

public class ReplayDeterminismTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"arda-determinism-{Guid.NewGuid():N}");

    private const string SampleLog =
        """
        [14:30:05] LocalPlayer: ProcessAddItem(TestItem(12345), -1, False)
        [14:30:05] LocalPlayer: ProcessDeleteItem(12345)
        [14:30:06] LocalPlayer: ProcessAddItem(AnotherItem(67890), -1, False)
        LoadAssetAsync: noise.Status=None.
        [14:30:07] LOADING LEVEL AreaSerbule
        [14:30:08] !!! Initializing area! (502934): AreaSerbule
        [14:30:09] LocalPlayer: ProcessAddItem(ThirdItem(11111), -1, False)
        """;

    public ReplayDeterminismTests()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, "Player.log"), SampleLog);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Replaying_same_log_twice_produces_identical_dispatch_sequences()
    {
        var first = await RunPipeline();
        var second = await RunPipeline();

        first.Should().NotBeEmpty("the log contains dispatchable lines");
        second.Should().Equal(first);
    }

    [Fact]
    public async Task Replaying_same_log_twice_produces_identical_metadata()
    {
        var first = await RunPipelineWithMetadata();
        var second = await RunPipelineWithMetadata();

        first.Should().NotBeEmpty();
        second.Should().Equal(first);
    }

    private async Task<List<(string Verb, string Args, string Source)>> RunPipeline()
    {
        var handler = new RecordingHandler();
        var table = BuildDispatchTable(handler);
        var source = new FiniteLogSource(Path.Combine(_tempDir, "Player.log"));
        var driver = new WorldDriver(source, table);

        await driver.RunAsync(CancellationToken.None);
        return handler.Dispatches;
    }

    private async Task<List<(string Verb, string Args, bool IsReplay)>> RunPipelineWithMetadata()
    {
        var handler = new MetadataRecordingHandler();
        var table = BuildDispatchTable(handler);
        var source = new FiniteLogSource(Path.Combine(_tempDir, "Player.log"));
        var driver = new WorldDriver(source, table);

        await driver.RunAsync(CancellationToken.None);
        return handler.Dispatches;
    }

    private static DispatchTable BuildDispatchTable(IFrameHandler handler)
    {
        var registry = new Dictionary<string, List<IFrameHandler>>
        {
            [Verbs.ProcessAddItem] = [handler],
            [Verbs.ProcessDeleteItem] = [handler],
            [Verbs.LoadingLevel] = [handler],
            [Verbs.InitializingArea] = [handler],
        };
        return new DispatchTable(registry, NullLogger<DispatchTable>.Instance);
    }

    /// <summary>
    /// A finite <see cref="ILogLineSource"/> that reads all lines from a file,
    /// strips the <c>[HH:MM:SS] </c> timestamp prefix, and terminates.
    /// Non-timestamped lines are discarded (matching L1 classifier behaviour
    /// for lines that aren't in the system-pattern allowlist).
    /// </summary>
    private sealed class FiniteLogSource(string filePath) : ILogLineSource
    {
        private const int TimestampPrefixLength = 11; // "[HH:MM:SS] "

        public async IAsyncEnumerable<LogLine> Lines(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var lines = await File.ReadAllLinesAsync(filePath, ct);
            var now = DateTimeOffset.UtcNow;

            foreach (var raw in lines)
            {
                if (raw.Length < TimestampPrefixLength)
                    continue;

                if (raw[0] != '[' || raw[3] != ':' || raw[6] != ':' || raw[9] != ']')
                    continue;

                if (!int.TryParse(raw.AsSpan(1, 2), out var h) ||
                    !int.TryParse(raw.AsSpan(4, 2), out var m) ||
                    !int.TryParse(raw.AsSpan(7, 2), out var s))
                    continue;

                var timestamp = new DateTimeOffset(
                    now.Year, now.Month, now.Day, h, m, s, TimeSpan.Zero);

                var stripped = raw[TimestampPrefixLength..];
                var metadata = new LogLineMetadata(timestamp, now, IsReplay: true);
                yield return new LogLine(stripped, metadata);
            }
        }
    }

    private sealed class RecordingHandler : IFrameHandler
    {
        public List<(string Verb, string Args, string Source)> Dispatches { get; } = [];

        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        {
            Dispatches.Add((verb.ToString(), args.ToString(), sourceLog));
        }
    }

    private sealed class MetadataRecordingHandler : IFrameHandler
    {
        public List<(string Verb, string Args, bool IsReplay)> Dispatches { get; } = [];

        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        {
            Dispatches.Add((verb.ToString(), args.ToString(), metadata.IsReplay));
        }
    }
}
