using Arda.Abstractions.Logs;
using Arda.Contracts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class DomainEventBusIntegrationTests
{
    private readonly record struct TestEventA(int Value, string Label);
    private readonly record struct TestEventB(double X);

    private sealed class PublishingHandler(IDomainEventPublisher bus) : IFrameHandler
    {
        public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
        {
            bus.Publish(new TestEventA(42, "from-handler"));
        }
    }

    private static DomainEventBus CreateBus() => new(NullLogger<DomainEventBus>.Instance);

    private static DispatchTable CreateTable(Dictionary<string, List<IFrameHandler>> registry) =>
        new(registry, NullLogger<DispatchTable>.Instance);

    private static LogLineMetadata DefaultMetadata =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, IsReplay: false);

    [Fact]
    public void MultipleSubscribers_AllReceiveEvent_WhenHandlerPublishes()
    {
        var bus = CreateBus();
        var receivedA = new List<TestEventA>();
        var receivedB = new List<TestEventA>();
        var receivedC = new List<TestEventA>();

        bus.Subscribe<TestEventA>(e => receivedA.Add(e));
        bus.Subscribe<TestEventA>(e => receivedB.Add(e));
        bus.Subscribe<TestEventA>(e => receivedC.Add(e));

        bus.Publish(new TestEventA(99, "multi"));

        var expected = new TestEventA(99, "multi");
        receivedA.Should().ContainSingle().Which.Should().Be(expected);
        receivedB.Should().ContainSingle().Which.Should().Be(expected);
        receivedC.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public void HandlerToBusToSubscriber_FullPipelineFlow()
    {
        var bus = CreateBus();
        var handler = new PublishingHandler(bus);

        var registry = new Dictionary<string, List<IFrameHandler>>
        {
            ["ProcessStartInteraction"] = [handler]
        };
        var table = CreateTable(registry);

        var received = new List<TestEventA>();
        bus.Subscribe<TestEventA>(e => received.Add(e));

        var line = "LocalPlayer: ProcessStartInteraction(some-args)".AsSpan();
        var parsed = VerbExtractor.Parse(line);

        table.Dispatch(parsed, "LocalPlayer: ProcessStartInteraction(some-args)", DefaultMetadata);

        received.Should().ContainSingle()
            .Which.Should().Be(new TestEventA(42, "from-handler"));
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotPreventOtherSubscribers()
    {
        var bus = CreateBus();
        var handler = new PublishingHandler(bus);

        var registry = new Dictionary<string, List<IFrameHandler>>
        {
            ["ProcessStartInteraction"] = [handler]
        };
        var table = CreateTable(registry);

        var beforeThrow = new List<TestEventA>();
        var afterThrow = new List<TestEventA>();

        bus.Subscribe<TestEventA>(e => beforeThrow.Add(e));
        bus.Subscribe<TestEventA>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<TestEventA>(e => afterThrow.Add(e));

        var line = "LocalPlayer: ProcessStartInteraction(x)".AsSpan();
        var parsed = VerbExtractor.Parse(line);

        table.Dispatch(parsed, "LocalPlayer: ProcessStartInteraction(x)", DefaultMetadata);

        var expected = new TestEventA(42, "from-handler");
        beforeThrow.Should().ContainSingle().Which.Should().Be(expected);
        afterThrow.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public void DisposedSubscription_NoLongerReceivesEvents()
    {
        var bus = CreateBus();
        var received = new List<TestEventA>();

        var sub = bus.Subscribe<TestEventA>(e => received.Add(e));

        bus.Publish(new TestEventA(1, "before"));
        sub.Dispose();
        bus.Publish(new TestEventA(2, "after"));

        received.Should().ContainSingle()
            .Which.Should().Be(new TestEventA(1, "before"));
    }

    [Fact]
    public void CrossTypeIsolation_PublishingTypeA_DoesNotTriggerTypeBSubscribers()
    {
        var bus = CreateBus();
        var handler = new PublishingHandler(bus);

        var registry = new Dictionary<string, List<IFrameHandler>>
        {
            ["ProcessStartInteraction"] = [handler]
        };
        var table = CreateTable(registry);

        var receivedB = new List<TestEventB>();
        bus.Subscribe<TestEventB>(e => receivedB.Add(e));

        var receivedA = new List<TestEventA>();
        bus.Subscribe<TestEventA>(e => receivedA.Add(e));

        var line = "LocalPlayer: ProcessStartInteraction(z)".AsSpan();
        var parsed = VerbExtractor.Parse(line);

        table.Dispatch(parsed, "LocalPlayer: ProcessStartInteraction(z)", DefaultMetadata);

        receivedA.Should().ContainSingle();
        receivedB.Should().BeEmpty();
    }
}
