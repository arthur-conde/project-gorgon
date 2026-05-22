using FluentAssertions;
using Mithril.WorldSim.Player.Internal;
using Xunit;

namespace Mithril.WorldSim.Player.Tests.Internal;

public sealed class WorldEventBusSubscriptionDisposeTests
{
    private static DateTimeOffset Ts(int sec) => new(2026, 1, 1, 12, 0, sec, TimeSpan.Zero);

    private sealed record Ping(string Tag);
    private sealed record Pong(string Tag);

    [Fact]
    public void Dispose_removes_subscription_from_per_type_list_and_handler_stops_firing()
    {
        var bus = new WorldEventBus();
        var observed = new List<Ping>();
        var sub = bus.Subscribe<Ping>(f => observed.Add(f.Payload));

        bus.SubscriberCountForTesting<Ping>().Should().Be(1);

        sub.Dispose();

        bus.SubscriberCountForTesting<Ping>().Should().Be(0);

        bus.Publish(new Frame<Ping>(Ts(1), new Ping("after-dispose")));

        observed.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_is_idempotent_under_double_dispose()
    {
        var bus = new WorldEventBus();
        var sub = bus.Subscribe<Ping>(_ => { });

        sub.Dispose();
        var act = () => sub.Dispose();

        act.Should().NotThrow();
        bus.SubscriberCountForTesting<Ping>().Should().Be(0);
    }

    [Fact]
    public void Disposing_one_of_many_subscriptions_leaves_the_others_intact()
    {
        var bus = new WorldEventBus();
        var a = new List<Ping>();
        var b = new List<Ping>();
        var c = new List<Ping>();

        var subA = bus.Subscribe<Ping>(f => a.Add(f.Payload));
        var subB = bus.Subscribe<Ping>(f => b.Add(f.Payload));
        var subC = bus.Subscribe<Ping>(f => c.Add(f.Payload));

        bus.SubscriberCountForTesting<Ping>().Should().Be(3);

        subB.Dispose();

        bus.SubscriberCountForTesting<Ping>().Should().Be(2);

        bus.Publish(new Frame<Ping>(Ts(1), new Ping("x")));

        a.Should().Equal(new Ping("x"));
        b.Should().BeEmpty();
        c.Should().Equal(new Ping("x"));
    }

    [Fact]
    public void Dispose_only_affects_the_disposed_subscriptions_payload_type()
    {
        var bus = new WorldEventBus();
        var pingSub = bus.Subscribe<Ping>(_ => { });
        var pongSub = bus.Subscribe<Pong>(_ => { });

        pingSub.Dispose();

        bus.SubscriberCountForTesting<Ping>().Should().Be(0);
        bus.SubscriberCountForTesting<Pong>().Should().Be(1);

        pongSub.Dispose();
    }
}
