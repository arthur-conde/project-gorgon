using Arda.Abstractions.Logs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class DispatchTableHaltTests
{
    private static LogLineMetadata Meta => new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsReplay: false);

    [Fact]
    public void GrammarException_PropagatesOutOfDispatch()
    {
        var table = new DispatchTable(
            new Dictionary<string, List<IFrameHandler>>
            {
                ["ProcessAddItem"] = [new ThrowingHandler(() => throw new GrammarException(
                    "ProcessAddItem", "source", "NOT_A_NUMBER", "expected long"))]
            },
            NullLogger<DispatchTable>.Instance);

        const string line = "LocalPlayer: ProcessAddItem(NOT_A_NUMBER)";
        var act = () => table.Dispatch(VerbExtractor.Parse(line.AsSpan()), line, Meta);

        act.Should().Throw<GrammarException>()
            .Which.Verb.Should().Be("ProcessAddItem");
    }

    [Fact]
    public void GenericException_IsSwallowedAndLogged()
    {
        var table = new DispatchTable(
            new Dictionary<string, List<IFrameHandler>>
            {
                ["ProcessAddItem"] = [new ThrowingHandler(() => throw new InvalidOperationException("boom"))]
            },
            NullLogger<DispatchTable>.Instance);

        const string line = "LocalPlayer: ProcessAddItem(123)";
        var act = () => table.Dispatch(VerbExtractor.Parse(line.AsSpan()), line, Meta);

        act.Should().NotThrow();
    }

    [Fact]
    public void GrammarException_AbortsRemainingHandlersForSameVerb()
    {
        var secondInvoked = false;
        var table = new DispatchTable(
            new Dictionary<string, List<IFrameHandler>>
            {
                ["ProcessAddItem"] =
                [
                    new ThrowingHandler(() => throw new GrammarException(
                        "ProcessAddItem", "source", "NOT_A_NUMBER", "expected long")),
                    new ThrowingHandler(() => secondInvoked = true)
                ]
            },
            NullLogger<DispatchTable>.Instance);

        const string line = "LocalPlayer: ProcessAddItem(NOT_A_NUMBER)";
        var act = () => table.Dispatch(VerbExtractor.Parse(line.AsSpan()), line, Meta);

        act.Should().Throw<GrammarException>();
        secondInvoked.Should().BeFalse("the GrammarException aborts the foreach over handlers");
    }

    private sealed class ThrowingHandler(Action onHandle) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => onHandle();
    }
}
