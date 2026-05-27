using Arda.Abstractions.Logs;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class DispatchTableTests
{
    private static readonly LogLineMetadata TestMetadata = new(
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsReplay: false);

    private static DispatchTable Build(Dictionary<string, List<IFrameHandler>> registry)
        => new(registry, NullLogger<DispatchTable>.Instance);

    [Fact]
    public void Dispatch_UnknownVerb_DoesNotThrow()
    {
        var table = Build(new Dictionary<string, List<IFrameHandler>>());

        var act = () => table.Dispatch(
            new ParsedVerb("ProcessUnknown".AsSpan(), "(123)".AsSpan()),
            "LocalPlayer: ProcessUnknown(123)",
            TestMetadata);

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispatch_EmptyVerb_DoesNotThrow()
    {
        var table = Build(new Dictionary<string, List<IFrameHandler>>());

        var act = () => table.Dispatch(
            default,
            "some random line",
            TestMetadata);

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispatch_SingleHandler_InvokesHandler()
    {
        var received = new List<string>();
        var handler = new TestHandler(args => received.Add(args.ToString()));

        var registry = new Dictionary<string, List<IFrameHandler>>
        {
            ["ProcessDeleteItem"] = [handler]
        };
        var table = Build(registry);

        table.Dispatch(
            VerbExtractor.Parse("LocalPlayer: ProcessDeleteItem(12345)".AsSpan()),
            "LocalPlayer: ProcessDeleteItem(12345)",
            TestMetadata);

        received.Should().ContainSingle().Which.Should().Be("(12345)");
    }

    [Fact]
    public void Dispatch_MultipleHandlers_InvokesInRegistrationOrder()
    {
        var order = new List<int>();
        var h1 = new TestHandler(_ => order.Add(1));
        var h2 = new TestHandler(_ => order.Add(2));
        var h3 = new TestHandler(_ => order.Add(3));

        var registry = new Dictionary<string, List<IFrameHandler>>
        {
            ["ProcessDeleteItem"] = [h1, h2, h3]
        };
        var table = Build(registry);

        table.Dispatch(
            VerbExtractor.Parse("LocalPlayer: ProcessDeleteItem(99)".AsSpan()),
            "LocalPlayer: ProcessDeleteItem(99)",
            TestMetadata);

        order.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Dispatch_ThrowingHandler_DoesNotPreventSubsequentHandlers()
    {
        var received = new List<int>();
        var h1 = new TestHandler(_ => received.Add(1));
        var hBad = new TestHandler(_ => throw new InvalidOperationException("boom"));
        var h3 = new TestHandler(_ => received.Add(3));

        var registry = new Dictionary<string, List<IFrameHandler>>
        {
            ["ProcessTest"] = [h1, hBad, h3]
        };
        var table = Build(registry);

        table.Dispatch(
            VerbExtractor.Parse("LocalPlayer: ProcessTest(x)".AsSpan()),
            "LocalPlayer: ProcessTest(x)",
            TestMetadata);

        received.Should().Equal(1, 3);
    }

    [Fact]
    public void Dispatch_SpanLookup_MatchesStringKey()
    {
        var called = false;
        var handler = new TestHandler(_ => called = true);

        var registry = new Dictionary<string, List<IFrameHandler>>
        {
            ["LOADING_LEVEL"] = [handler]
        };
        var table = Build(registry);

        table.Dispatch(
            VerbExtractor.Parse("LOADING LEVEL AreaSerbule".AsSpan()),
            "LOADING LEVEL AreaSerbule",
            TestMetadata);

        called.Should().BeTrue();
    }

    private sealed class TestHandler(Action<ReadOnlySpan<char>> onHandle) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
            => onHandle(args);
    }
}
