using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Dispatch.Tests;

public class DomainEventBusTests
{
    private readonly DomainEventBus _bus = new(NullLogger<DomainEventBus>.Instance);

    private readonly record struct TestEvent(int Value);
    private readonly record struct OtherEvent(string Name);

    [Fact]
    public void Publish_WithSubscriber_DeliversEvent()
    {
        var received = new List<TestEvent>();
        _bus.Subscribe<TestEvent>(e => received.Add(e));

        _bus.Publish(new TestEvent(42));

        received.Should().ContainSingle().Which.Value.Should().Be(42);
    }

    [Fact]
    public void Publish_MultipleSubscribers_AllReceive()
    {
        var r1 = new List<TestEvent>();
        var r2 = new List<TestEvent>();
        _bus.Subscribe<TestEvent>(e => r1.Add(e));
        _bus.Subscribe<TestEvent>(e => r2.Add(e));

        _bus.Publish(new TestEvent(7));

        r1.Should().ContainSingle().Which.Value.Should().Be(7);
        r2.Should().ContainSingle().Which.Value.Should().Be(7);
    }

    [Fact]
    public void Publish_NoSubscribers_DoesNotThrow()
    {
        var act = () => _bus.Publish(new TestEvent(1));
        act.Should().NotThrow();
    }

    [Fact]
    public void Publish_DifferentEventTypes_AreIsolated()
    {
        var testEvents = new List<TestEvent>();
        var otherEvents = new List<OtherEvent>();
        _bus.Subscribe<TestEvent>(e => testEvents.Add(e));
        _bus.Subscribe<OtherEvent>(e => otherEvents.Add(e));

        _bus.Publish(new TestEvent(1));
        _bus.Publish(new OtherEvent("hello"));

        testEvents.Should().ContainSingle().Which.Value.Should().Be(1);
        otherEvents.Should().ContainSingle().Which.Name.Should().Be("hello");
    }

    [Fact]
    public void Unsubscribe_ViaDispose_StopsDelivery()
    {
        var received = new List<TestEvent>();
        var sub = _bus.Subscribe<TestEvent>(e => received.Add(e));

        _bus.Publish(new TestEvent(1));
        sub.Dispose();
        _bus.Publish(new TestEvent(2));

        received.Should().ContainSingle().Which.Value.Should().Be(1);
    }

    [Fact]
    public void Unsubscribe_DuringPublish_DoesNotAffectCurrentBatch()
    {
        var received = new List<int>();
        IDisposable? sub2 = null;

        _bus.Subscribe<TestEvent>(_ => received.Add(1));
        sub2 = _bus.Subscribe<TestEvent>(_ =>
        {
            received.Add(2);
            sub2!.Dispose();
        });
        _bus.Subscribe<TestEvent>(_ => received.Add(3));

        _bus.Publish(new TestEvent(0));

        // All three fire because Publish uses a snapshot
        received.Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Publish_ThrowingSubscriber_DoesNotPreventOthers()
    {
        var received = new List<int>();
        _bus.Subscribe<TestEvent>(_ => received.Add(1));
        _bus.Subscribe<TestEvent>(_ => throw new InvalidOperationException("boom"));
        _bus.Subscribe<TestEvent>(_ => received.Add(3));

        _bus.Publish(new TestEvent(0));

        received.Should().Equal(1, 3);
    }

    // NOTE: This test pins the subscriber-attribution warning that DomainEventBus
    // emits before re-throwing a GrammarException — operators rely on it to
    // reconstruct the actual source of a grammar break, since DispatchTable
    // attributes the throw to the publishing handler's frame. Delete this test
    // (and the corresponding LogWarning in DomainEventBus.Publish) when the
    // structural fix in moumantai-gg/mithril#817 lands.
    [Fact]
    public void Publish_SubscriberThrowsGrammarException_PropagatesAndLogsSubscriberIdentity()
    {
        var recorder = new RecordingLogger();
        var bus = new DomainEventBus(recorder);

        var subscriber = new GrammarThrowingSubscriber();
        bus.Subscribe<TestEvent>(subscriber.OnEvent);

        var act = () => bus.Publish(new TestEvent(99));

        act.Should().Throw<GrammarException>()
            .WithMessage("*Grammar drift on verb ProcessTest*");

        recorder.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning)
            .Which.State.Should().Match<IReadOnlyList<KeyValuePair<string, object?>>>(
                pairs =>
                    pairs.Any(p => p.Key == "Target" && Equals(p.Value, nameof(GrammarThrowingSubscriber)))
                    && pairs.Any(p => p.Key == "Method" && Equals(p.Value, nameof(GrammarThrowingSubscriber.OnEvent)))
                    && pairs.Any(p => p.Key == "EventType" && Equals(p.Value, nameof(TestEvent))));
    }

    private sealed class GrammarThrowingSubscriber
    {
        public void OnEvent(TestEvent evt)
            => throw new GrammarException(
                verb: "ProcessTest",
                sourceLine: "LocalPlayer: ProcessTest(\"x\")",
                tokenExcerpt: "x",
                parserHint: "subscriber bug — synthetic");
    }

    private sealed class RecordingLogger : ILogger<DomainEventBus>
    {
        public List<LogRecord> Records { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Capture the structured state (the named-parameter pairs MEL builds
            // from the message template). Snapshot-list rather than holding TState
            // by reference since some loggers reuse the buffer.
            var pairs = state as IReadOnlyList<KeyValuePair<string, object?>>
                ?? [new KeyValuePair<string, object?>("{OriginalFormat}", formatter(state, exception))];
            Records.Add(new LogRecord(logLevel, pairs.ToList(), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed record LogRecord(
        LogLevel Level,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        Exception? Exception);

    [Fact]
    public void Subscribe_AfterPublish_ReceivesSubsequentEvents()
    {
        var received = new List<TestEvent>();

        _bus.Publish(new TestEvent(1)); // no subscriber yet
        _bus.Subscribe<TestEvent>(e => received.Add(e));
        _bus.Publish(new TestEvent(2));

        received.Should().ContainSingle().Which.Value.Should().Be(2);
    }

    [Fact]
    public void Dispose_SameHandlerTwice_DoesNotThrow()
    {
        var sub = _bus.Subscribe<TestEvent>(_ => { });
        sub.Dispose();

        var act = () => sub.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_Idempotent_DoesNotRemoveOtherSubscription()
    {
        var received = new List<int>();
        Action<TestEvent> handler = e => received.Add(e.Value);

        var sub1 = _bus.Subscribe(handler);
        var sub2 = _bus.Subscribe(handler);

        sub1.Dispose();
        sub1.Dispose(); // second dispose must be a no-op

        _bus.Publish(new TestEvent(42));

        received.Should().ContainSingle().Which.Should().Be(42);
    }
}
