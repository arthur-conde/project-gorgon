using System.Runtime.CompilerServices;
using Arda.Abstractions.Logs;
using Arda.Contracts.State.Health;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class WorldDriverHaltTests
{
    [Fact]
    public async Task GrammarException_RaisesSignalAndExitsLoop()
    {
        var signal = new GrammarBreakSignal();
        var liveFired = false;

        var table = new DispatchTable(
            new Dictionary<string, List<IFrameHandler>>
            {
                ["ProcessAddItem"] = [new ThrowOnce(() => throw new GrammarException(
                    "ProcessAddItem", "source", "NOT_A_NUMBER", "expected long"))]
            },
            NullLogger<DispatchTable>.Instance);

        var lines = new[]
        {
            MakeLine("LocalPlayer: ProcessAddItem(NOT_A_NUMBER)", isReplay: true),
            MakeLine("LocalPlayer: ProcessAddItem(123)", isReplay: true)
        };

        var driver = new WorldDriver(
            new InMemorySource(lines),
            table,
            onLiveTransition: () => liveFired = true,
            sourceFamily: "Player",
            grammarSignal: signal);

        await driver.RunAsync(CancellationToken.None);

        signal.IsRaised.Should().BeTrue();
        signal.Current!.Verb.Should().Be("ProcessAddItem");
        signal.Current.SourceFamily.Should().Be("Player");
        liveFired.Should().BeFalse(
            "halt during replay must not force the end-of-stream live transition, " +
            "which would resolve replay-complete latches and trigger snapshot writes against divergent state");
    }

    [Fact]
    public async Task CompanionDriverHalt_StopsThisDriverAtNextLoopIteration()
    {
        var signal = new GrammarBreakSignal();
        signal.Raise(new GrammarBreak("Other", "ProcessFoo", "src", "x", "hint", DateTimeOffset.UtcNow));

        var processed = 0;
        var table = new DispatchTable(
            new Dictionary<string, List<IFrameHandler>>
            {
                ["ProcessAddItem"] = [new CountingHandler(() => processed++)]
            },
            NullLogger<DispatchTable>.Instance);

        var lines = new[]
        {
            MakeLine("LocalPlayer: ProcessAddItem(1)"),
            MakeLine("LocalPlayer: ProcessAddItem(2)")
        };

        var driver = new WorldDriver(
            new InMemorySource(lines),
            table,
            sourceFamily: "Chat",
            grammarSignal: signal);

        await driver.RunAsync(CancellationToken.None);

        processed.Should().Be(0, "driver halts before processing any line when companion already raised the signal");
    }

    [Fact]
    public async Task TolerantMode_LogsAndContinuesPastGrammarBreak()
    {
        var signal = new GrammarBreakSignal();
        var processed = 0;

        var table = new DispatchTable(
            new Dictionary<string, List<IFrameHandler>>
            {
                ["ProcessAddItem"] = [new ConditionalThrow(
                    shouldThrow: args => args.Contains("NOT_A_NUMBER"),
                    onSuccess: () => processed++)]
            },
            NullLogger<DispatchTable>.Instance);

        var lines = new[]
        {
            MakeLine("LocalPlayer: ProcessAddItem(NOT_A_NUMBER)"),
            MakeLine("LocalPlayer: ProcessAddItem(123)")
        };

        var driver = new WorldDriver(
            new InMemorySource(lines),
            table,
            sourceFamily: "Player",
            grammarSignal: signal,
            tolerantGrammar: true);

        await driver.RunAsync(CancellationToken.None);

        signal.IsRaised.Should().BeFalse("tolerant mode suppresses the halt signal");
        processed.Should().Be(1, "the second (well-formed) line still runs through the handler");
    }

    private static LogLine MakeLine(string log, bool isReplay = false) =>
        new(log, new LogLineMetadata(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsReplay: isReplay));

    private sealed class InMemorySource(IEnumerable<LogLine> lines) : ILogLineSource
    {
        public async IAsyncEnumerable<LogLine> Lines([EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var line in lines)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return line;
                await Task.Yield();
            }
        }
    }

    private sealed class ThrowOnce(Action onHandle) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => onHandle();
    }

    private sealed class CountingHandler(Action onHandle) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => onHandle();
    }

    private sealed class ConditionalThrow(Func<string, bool> shouldThrow, Action onSuccess) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        {
            if (shouldThrow(args.ToString()))
                throw new GrammarException(verb.ToString(), sourceLog, "NOT_A_NUMBER", "expected long");
            onSuccess();
        }
    }
}
