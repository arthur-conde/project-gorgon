using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class VendorPassthroughTests
{
    private readonly SpyEventBus _bus = new();

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    // ── VendorScreenHandler ──────────────────────────────────────────────

    [Fact]
    public void VendorScreen_ParsesAllFields()
    {
        var handler = new VendorScreenHandler(_bus);
        var args = "(-162, Comfortable, 5000, 3, 10000, \"desc\", [], stuff)";

        handler.Handle(args.AsSpan(), "", Meta());

        var e = _bus.Published<VendorScreenOpened>().Should().ContainSingle().Which;
        e.EntityId.Should().Be(-162);
        e.FavorTier.Should().Be("Comfortable");
        e.RemainingGold.Should().Be(5000);
        e.GoldCap.Should().Be(10000);
    }

    [Fact]
    public void VendorScreen_NegativeEntityId()
    {
        var handler = new VendorScreenHandler(_bus);
        var args = "(-999, Neutral, 0, 0, 5000)";

        handler.Handle(args.AsSpan(), "", Meta());

        var e = _bus.Published<VendorScreenOpened>().Should().ContainSingle().Which;
        e.EntityId.Should().Be(-999);
        e.FavorTier.Should().Be("Neutral");
        e.RemainingGold.Should().Be(0);
        e.GoldCap.Should().Be(5000);
    }

    // ── VendorAddItemHandler ─────────────────────────────────────────────

    [Fact]
    public void VendorAddItem_ParsesNameAndInstanceId()
    {
        var handler = new VendorAddItemHandler(_bus);
        var args = "(250, SwordNovice(12345), True)";

        handler.Handle(args.AsSpan(), "", Meta());

        var e = _bus.Published<VendorItemSold>().Should().ContainSingle().Which;
        e.Price.Should().Be(250);
        e.InternalName.Should().Be("SwordNovice");
        e.InstanceId.Should().Be(12345);
    }

    [Fact]
    public void VendorAddItem_LargePrice()
    {
        var handler = new VendorAddItemHandler(_bus);
        var args = "(999999, RareItem(8888), False)";

        handler.Handle(args.AsSpan(), "", Meta());

        var e = _bus.Published<VendorItemSold>().Should().ContainSingle().Which;
        e.Price.Should().Be(999999);
        e.InternalName.Should().Be("RareItem");
        e.InstanceId.Should().Be(8888);
    }

    [Fact]
    public void VendorAddItem_IgnoresInvalidFormat()
    {
        var handler = new VendorAddItemHandler(_bus);
        var args = "(250, NoParenthesis, True)";

        handler.Handle(args.AsSpan(), "", Meta());

        _bus.Published<VendorItemSold>().Should().BeEmpty();
    }

    // ── VendorGoldHandler ────────────────────────────────────────────────

    [Fact]
    public void VendorGold_ParsesGoldAndCap()
    {
        var handler = new VendorGoldHandler(_bus);
        var args = "(4500, 3, 10000)";

        handler.Handle(args.AsSpan(), "", Meta());

        var e = _bus.Published<VendorGoldUpdated>().Should().ContainSingle().Which;
        e.RemainingGold.Should().Be(4500);
        e.GoldCap.Should().Be(10000);
    }

    [Fact]
    public void VendorGold_ZeroValues()
    {
        var handler = new VendorGoldHandler(_bus);
        var args = "(0, 0, 0)";

        handler.Handle(args.AsSpan(), "", Meta());

        var e = _bus.Published<VendorGoldUpdated>().Should().ContainSingle().Which;
        e.RemainingGold.Should().Be(0);
        e.GoldCap.Should().Be(0);
    }

    // ── SpyEventBus ─────────────────────────────────────────────────────

    private sealed class SpyEventBus : IDomainEventBus
    {
        private readonly Dictionary<Type, List<object>> _published = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
            => new NoopDisposable();

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (!_published.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _published[typeof(T)] = list;
            }
            list.Add(domainEvent);
        }

        public List<T> Published<T>() where T : struct
        {
            if (_published.TryGetValue(typeof(T), out var list))
                return list.Cast<T>().ToList();
            return [];
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
