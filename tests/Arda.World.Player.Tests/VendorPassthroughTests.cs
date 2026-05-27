using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class VendorPassthroughTests
{
    private readonly SpyBus _bus = new();
    private readonly Npc _npc;

    public VendorPassthroughTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _npc = new Npc(_bus, pool);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    private void ArmNpcInteraction(string npcKey, long entityId = 12307)
    {
        _npc.OnStartInteraction($"({entityId}, 7, 2405.813, True, \"{npcKey}\")".AsSpan(), default, $"LocalPlayer: ProcessStartInteraction({entityId}, 7, 2405.813, True, \"{npcKey}\")", Meta());
        _bus.Clear();
    }

    // ── VendorScreenHandler ──────────────────────────────────────────────

    [Fact]
    public void VendorScreen_ParsesAllFields()
    {
        var handler = new VendorScreenHandler(_npc);
        var args = "(-162, Comfortable, 5000, 1779404053485, 10000, \"desc\", [], stuff)";

        handler.Handle(args.AsSpan(), default, "", Meta());

        var e = _bus.Published<VendorScreenOpened>().Should().ContainSingle().Which;
        e.EntityId.Should().Be(-162);
        e.FavorTier.Should().Be("Comfortable");
        e.RemainingGold.Should().Be(5000);
        e.GoldCap.Should().Be(10000);
        e.GoldResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1779404053485));
        e.NpcKey.Should().BeNull(because: "no prior interaction was armed");
    }

    [Fact]
    public void VendorScreen_WithActiveInteraction_EnrichesNpcKey()
    {
        ArmNpcInteraction("NPC_Therese", entityId: 14564);

        var handler = new VendorScreenHandler(_npc);
        handler.Handle("(14564, Neutral, 3926, 1779404053485, 4000)".AsSpan(), default, "", Meta());

        var e = _bus.Published<VendorScreenOpened>().Should().ContainSingle().Which;
        e.EntityId.Should().Be(14564);
        e.NpcKey.Should().Be("NPC_Therese");
        e.FavorTier.Should().Be("Neutral");
    }

    [Fact]
    public void VendorScreen_EntityMismatch_NullNpcKey()
    {
        ArmNpcInteraction("NPC_Therese", entityId: 14564);

        var handler = new VendorScreenHandler(_npc);
        handler.Handle("(99999, Neutral, 3926, 1779404053485, 4000)".AsSpan(), default, "", Meta());

        var e = _bus.Published<VendorScreenOpened>().Should().ContainSingle().Which;
        e.EntityId.Should().Be(99999);
        e.NpcKey.Should().BeNull();
    }

    [Fact]
    public void VendorScreen_NegativeEntityId()
    {
        var handler = new VendorScreenHandler(_npc);
        var args = "(-999, Neutral, 0, 1779404053485, 5000)";

        handler.Handle(args.AsSpan(), default, "", Meta());

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
        var handler = new VendorAddItemHandler(_npc);
        var args = "(250, SwordNovice(12345), True)";

        handler.Handle(args.AsSpan(), default, "", Meta());

        var e = _bus.Published<VendorItemSold>().Should().ContainSingle().Which;
        e.Price.Should().Be(250);
        e.InternalName.Should().Be("SwordNovice");
        e.InstanceId.Should().Be(12345);
        e.NpcKey.Should().BeNull(because: "no vendor session was established");
        e.FavorTier.Should().BeNull();
    }

    [Fact]
    public void VendorAddItem_WithVendorSession_EnrichesNpcKeyAndFavorTier()
    {
        ArmNpcInteraction("NPC_Johen", entityId: 9999);
        _npc.OnVendorScreen("(9999, Friendly, 5000, 1779404053485, 8000)".AsSpan(), default, "", Meta());
        _bus.Clear();

        var handler = new VendorAddItemHandler(_npc);
        handler.Handle("(250, SwordNovice(12345), True)".AsSpan(), default, "", Meta());

        var e = _bus.Published<VendorItemSold>().Should().ContainSingle().Which;
        e.Price.Should().Be(250);
        e.InternalName.Should().Be("SwordNovice");
        e.InstanceId.Should().Be(12345);
        e.NpcKey.Should().Be("NPC_Johen");
        e.FavorTier.Should().Be("Friendly");
    }

    [Fact]
    public void VendorAddItem_LargePrice()
    {
        var handler = new VendorAddItemHandler(_npc);
        var args = "(999999, RareItem(8888), False)";

        handler.Handle(args.AsSpan(), default, "", Meta());

        var e = _bus.Published<VendorItemSold>().Should().ContainSingle().Which;
        e.Price.Should().Be(999999);
        e.InternalName.Should().Be("RareItem");
        e.InstanceId.Should().Be(8888);
    }

    [Fact]
    public void VendorAddItem_IgnoresInvalidFormat()
    {
        var handler = new VendorAddItemHandler(_npc);
        var args = "(250, NoParenthesis, True)";

        handler.Handle(args.AsSpan(), default, "", Meta());

        _bus.Published<VendorItemSold>().Should().BeEmpty();
    }

    // ── VendorGoldHandler (unchanged passthrough) ─────────────────────────

    [Fact]
    public void VendorGold_ParsesGoldCapAndResetsAt()
    {
        var handler = new VendorGoldHandler(_bus);
        var args = "(4500, 1779404053485, 10000)";

        handler.Handle(args.AsSpan(), default, "", Meta());

        var e = _bus.Published<VendorGoldUpdated>().Should().ContainSingle().Which;
        e.RemainingGold.Should().Be(4500);
        e.GoldCap.Should().Be(10000);
        e.GoldResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1779404053485));
    }

    [Fact]
    public void VendorGold_ZeroValues()
    {
        var handler = new VendorGoldHandler(_bus);
        var args = "(0, 0, 0)";

        handler.Handle(args.AsSpan(), default, "", Meta());

        var e = _bus.Published<VendorGoldUpdated>().Should().ContainSingle().Which;
        e.RemainingGold.Should().Be(0);
        e.GoldCap.Should().Be(0);
        e.GoldResetsAt.Should().Be(DateTimeOffset.UnixEpoch);
    }

    // ── SpyBus ─────────────────────────────────────────────────────────

    private sealed class SpyBus : IDomainEventSubscriber, IDomainEventPublisher
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

        public void Clear() => _published.Clear();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
