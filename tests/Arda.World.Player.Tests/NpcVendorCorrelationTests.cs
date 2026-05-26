using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

/// <summary>
/// Tests the vendor session enrichment in <see cref="Npc"/>. The Npc handler
/// captures entity-to-NPC context from <c>ProcessStartInteraction</c> and
/// propagates it through <c>ProcessVendorScreen</c> → <c>ProcessVendorAddItem</c>.
/// Parallel to <see cref="NpcGiftCorrelationTests"/> for the gift FSM.
/// </summary>
public class NpcVendorCorrelationTests
{
    private readonly SpyBus _bus = new();
    private readonly Npc _npc;

    public NpcVendorCorrelationTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _npc = new Npc(_bus, pool);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    private void ArmNpcInteraction(string npcKey, long entityId = 12307)
    {
        _npc.OnStartInteraction(
            $"({entityId}, 7, 2405.813, True, \"{npcKey}\")".AsSpan(),
            $"LocalPlayer: ProcessStartInteraction({entityId}, 7, 2405.813, True, \"{npcKey}\")",
            Meta());
        _bus.Clear();
    }

    private void OpenVendorScreen(long entityId, string favorTier, long gold = 3926, long cap = 4000) =>
        _npc.OnVendorScreen($"({entityId}, {favorTier}, {gold}, 3, {cap})".AsSpan(), "", Meta());

    private void SellItem(long price, string internalName, long instanceId) =>
        _npc.OnVendorAddItem($"({price}, {internalName}({instanceId}), True)".AsSpan(), "", Meta());

    // ── Full flow: interaction → screen → sell ────────────────────────────

    [Fact]
    public void FullFlow_EnrichesVendorItemSoldWithNpcKeyAndFavorTier()
    {
        ArmNpcInteraction("NPC_Therese", entityId: 14564);
        OpenVendorScreen(14564, "Neutral");
        _bus.Clear();

        SellItem(130, "BottleOfWater", 78177652);

        var e = _bus.Published<VendorItemSold>().Should().ContainSingle().Which;
        e.NpcKey.Should().Be("NPC_Therese");
        e.FavorTier.Should().Be("Neutral");
        e.Price.Should().Be(130);
        e.InternalName.Should().Be("BottleOfWater");
        e.InstanceId.Should().Be(78177652);
    }

    [Fact]
    public void FullFlow_VendorScreenOpened_CarriesNpcKey()
    {
        ArmNpcInteraction("NPC_Therese", entityId: 14564);

        OpenVendorScreen(14564, "Comfortable");

        var e = _bus.Published<VendorScreenOpened>().Should().ContainSingle().Which;
        e.NpcKey.Should().Be("NPC_Therese");
        e.EntityId.Should().Be(14564);
        e.FavorTier.Should().Be("Comfortable");
    }

    // ── Multiple sells in one vendor session ──────────────────────────────

    [Fact]
    public void MultipleSells_SameSession_AllEnriched()
    {
        ArmNpcInteraction("NPC_Johen", entityId: 9999);
        OpenVendorScreen(9999, "Friendly");
        _bus.Clear();

        SellItem(100, "SwordNovice", 1001);
        SellItem(200, "ShieldBronze", 1002);
        SellItem(300, "PotionHealth", 1003);

        var sold = _bus.Published<VendorItemSold>();
        sold.Should().HaveCount(3);
        sold.Should().AllSatisfy(e =>
        {
            e.NpcKey.Should().Be("NPC_Johen");
            e.FavorTier.Should().Be("Friendly");
        });
    }

    // ── Missing interaction context ───────────────────────────────────────

    [Fact]
    public void VendorScreenWithoutInteraction_NullNpcKey()
    {
        OpenVendorScreen(14564, "Neutral");

        var e = _bus.Published<VendorScreenOpened>().Should().ContainSingle().Which;
        e.NpcKey.Should().BeNull();
    }

    [Fact]
    public void SellWithoutVendorScreen_NullContextFields()
    {
        SellItem(130, "BottleOfWater", 78177652);

        var e = _bus.Published<VendorItemSold>().Should().ContainSingle().Which;
        e.NpcKey.Should().BeNull();
        e.FavorTier.Should().BeNull();
    }

    // ── Entity ID mismatch ────────────────────────────────────────────────

    [Fact]
    public void VendorScreen_EntityMismatch_NullNpcKey()
    {
        ArmNpcInteraction("NPC_Therese", entityId: 14564);

        OpenVendorScreen(99999, "Neutral");

        var e = _bus.Published<VendorScreenOpened>().Should().ContainSingle().Which;
        e.NpcKey.Should().BeNull(because: "entity 99999 doesn't match active entity 14564");
    }

    // ── New interaction clears vendor session ─────────────────────────────

    [Fact]
    public void NewInteraction_ClearsVendorSession()
    {
        ArmNpcInteraction("NPC_Therese", entityId: 14564);
        OpenVendorScreen(14564, "Neutral");
        _bus.Clear();

        ArmNpcInteraction("NPC_Johen", entityId: 9999);

        SellItem(130, "BottleOfWater", 78177652);

        var e = _bus.Published<VendorItemSold>().Should().ContainSingle().Which;
        e.NpcKey.Should().BeNull(because: "the new interaction should have cleared the vendor session");
        e.FavorTier.Should().BeNull();
    }

    // ── Reset clears vendor session ───────────────────────────────────────

    [Fact]
    public void Reset_ClearsVendorSession()
    {
        ArmNpcInteraction("NPC_Therese", entityId: 14564);
        OpenVendorScreen(14564, "Neutral");
        _bus.Clear();

        _npc.Reset();

        SellItem(130, "BottleOfWater", 78177652);

        var e = _bus.Published<VendorItemSold>().Should().ContainSingle().Which;
        e.NpcKey.Should().BeNull();
        e.FavorTier.Should().BeNull();
    }

    // ── Switching NPCs mid-session ────────────────────────────────────────

    [Fact]
    public void SwitchingNpcs_PicksUpNewContext()
    {
        ArmNpcInteraction("NPC_Therese", entityId: 14564);
        OpenVendorScreen(14564, "Neutral");
        _bus.Clear();

        SellItem(100, "SwordNovice", 1001);
        _bus.Published<VendorItemSold>()[0].NpcKey.Should().Be("NPC_Therese");
        _bus.Clear();

        ArmNpcInteraction("NPC_Johen", entityId: 9999);
        OpenVendorScreen(9999, "Comfortable");
        _bus.Clear();

        SellItem(200, "ShieldBronze", 1002);
        _bus.Published<VendorItemSold>()[0].NpcKey.Should().Be("NPC_Johen");
        _bus.Published<VendorItemSold>()[0].FavorTier.Should().Be("Comfortable");
    }

    // ── SpyBus ───────────────────────────────────────────────────────────

    private sealed class SpyBus : IDomainEventBus
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
