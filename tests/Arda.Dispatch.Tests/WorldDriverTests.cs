using System.Runtime.CompilerServices;
using Arda.Abstractions.Logs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class WorldDriverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task RunAsync_DispatchesAllLines()
    {
        var lines = new[]
        {
            MakeLine("LocalPlayer: ProcessDeleteItem(1)", isReplay: false),
            MakeLine("LocalPlayer: ProcessAddItem(2)", isReplay: false),
        };
        var dispatched = new List<string>();
        var handler = new RecordingHandler(dispatched);
        var table = BuildTable(("ProcessDeleteItem", handler), ("ProcessAddItem", handler));
        var driver = new WorldDriver(new FakeSource(lines), table);

        await driver.RunAsync(CancellationToken.None);

        dispatched.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunAsync_OnLiveTransition_FiresOnFirstNonReplayLine()
    {
        var lines = new[]
        {
            MakeLine("LocalPlayer: ProcessA()", isReplay: true),
            MakeLine("LocalPlayer: ProcessB()", isReplay: true),
            MakeLine("LocalPlayer: ProcessC()", isReplay: false),
            MakeLine("LocalPlayer: ProcessD()", isReplay: false),
        };
        var transitionCount = 0;
        var table = BuildTable();
        var driver = new WorldDriver(new FakeSource(lines), table, () => transitionCount++);

        await driver.RunAsync(CancellationToken.None);

        transitionCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_AllReplay_FiresOnLiveTransitionAtEnd()
    {
        var lines = new[]
        {
            MakeLine("LocalPlayer: ProcessA()", isReplay: true),
            MakeLine("LocalPlayer: ProcessB()", isReplay: true),
        };
        var fired = false;
        var table = BuildTable();
        var driver = new WorldDriver(new FakeSource(lines), table, () => fired = true);

        await driver.RunAsync(CancellationToken.None);

        fired.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_EmptySource_FiresOnLiveTransition()
    {
        var fired = false;
        var table = BuildTable();
        var driver = new WorldDriver(new FakeSource([]), table, () => fired = true);

        await driver.RunAsync(CancellationToken.None);

        fired.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_NoCallback_DoesNotThrow()
    {
        var lines = new[]
        {
            MakeLine("LocalPlayer: ProcessA()", isReplay: true),
            MakeLine("LocalPlayer: ProcessB()", isReplay: false),
        };
        var table = BuildTable();
        var driver = new WorldDriver(new FakeSource(lines), table, onLiveTransition: null);

        var act = () => driver.RunAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_CancelledMidReplay_DoesNotForceLiveTransition()
    {
        var fired = false;
        var table = BuildTable();
        using var cts = new CancellationTokenSource();
        var driver = new WorldDriver(new InfiniteReplaySource(cts), table, () => fired = true);

        var run = driver.RunAsync(cts.Token);
        // Let the source yield a few replay lines, then cancel.
        await Task.Delay(50);
        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        fired.Should().BeFalse(
            "cancellation during replay must not resolve replay-complete latches — that would trigger flush-on-replay subscribers to write partial snapshots");
    }

    private static LogLine MakeLine(string log, bool isReplay)
        => new(log, new LogLineMetadata(Now, Now, isReplay));

    private static DispatchTable BuildTable(params (string verb, IFrameHandler handler)[] entries)
    {
        var registry = new Dictionary<string, List<IFrameHandler>>();
        foreach (var (verb, handler) in entries)
        {
            if (!registry.TryGetValue(verb, out var list))
            {
                list = [];
                registry[verb] = list;
            }
            list.Add(handler);
        }
        return new DispatchTable(registry, NullLogger<DispatchTable>.Instance);
    }

    private sealed class FakeSource(LogLine[] lines) : ILogLineSource
    {
        public async IAsyncEnumerable<LogLine> Lines(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var line in lines)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return line;
            }
            await Task.CompletedTask;
        }
    }

    private sealed class InfiniteReplaySource(CancellationTokenSource externalCts) : ILogLineSource
    {
        public async IAsyncEnumerable<LogLine> Lines(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested && !externalCts.IsCancellationRequested)
            {
                yield return MakeLine("LocalPlayer: ProcessReplay()", isReplay: true);
                try { await Task.Delay(5, ct); } catch (OperationCanceledException) { yield break; }
            }
        }
    }

    private sealed class RecordingHandler(List<string> log) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
            => log.Add(sourceLog);
    }
}
