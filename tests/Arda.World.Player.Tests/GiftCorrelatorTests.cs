using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class GiftCorrelatorTests : IDisposable
{
    private readonly LiveTestBus _bus = new();
    private readonly GiftCorrelator _correlator;
    private readonly List<GiftAccepted> _accepted = [];

    public GiftCorrelatorTests()
    {
        _correlator = new GiftCorrelator(_bus);
        _bus.Subscribe<GiftAccepted>(e => _accepted.Add(e));
    }

    public void Dispose() => _correlator.Dispose();

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    private void AddItem(long instanceId, string internalName) =>
        _bus.Publish(new InventoryItemAdded(instanceId, internalName, Meta()));

    private void GiftAttempt(long entityId, string npcKey, long instanceId) =>
        _bus.Publish(new GiftAttempted(entityId, npcKey, instanceId, Meta()));

    private void DeltaFavor(string npcKey, double delta) =>
        _bus.Publish(new DeltaFavorReceived(npcKey, delta, Meta()));

    private void StartInteraction(long entityId, string name, double favor, bool isNpc) =>
        _bus.Publish(new InteractionStarted(entityId, name, favor, isNpc, Meta()));

    // ── Delete-first correlation ─────────────────────────────────────────

    [Fact]
    public void DeleteFirst_ThenDelta_EmitsGiftAccepted()
    {
        AddItem(100, "Item_Apple");
        GiftAttempt(12307, "NPC_Joe", 100);
        DeltaFavor("NPC_Joe", 25.5);

        _accepted.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            EntityId = 12307L,
            NpcKey = "NPC_Joe",
            ItemInstanceId = 100L,
            ItemInternalName = "Item_Apple",
            DeltaFavor = 25.5
        });
    }

    // ── Delta-first correlation ──────────────────────────────────────────

    [Fact]
    public void DeltaFirst_ThenDelete_EmitsGiftAccepted()
    {
        AddItem(200, "Item_Sword");
        DeltaFavor("NPC_Way", 10.0);
        GiftAttempt(8887, "NPC_Way", 200);

        _accepted.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            EntityId = 8887L,
            NpcKey = "NPC_Way",
            ItemInstanceId = 200L,
            ItemInternalName = "Item_Sword",
            DeltaFavor = 10.0
        });
    }

    // ── New interaction clears pending ───────────────────────────────────

    [Fact]
    public void NewInteraction_ClearsPendingDelete()
    {
        AddItem(300, "Item_Gem");
        GiftAttempt(12307, "NPC_Joe", 300);

        StartInteraction(9999, "NPC_Other", 0, true);
        DeltaFavor("NPC_Joe", 5.0);

        _accepted.Should().BeEmpty();
    }

    [Fact]
    public void NewInteraction_ClearsPendingDelta()
    {
        AddItem(400, "Item_Ring");
        DeltaFavor("NPC_Joe", 15.0);

        StartInteraction(9999, "NPC_Other", 0, true);
        GiftAttempt(12307, "NPC_Joe", 400);

        _accepted.Should().BeEmpty();
    }

    // ── Unresolved instanceId ────────────────────────────────────────────

    [Fact]
    public void UnresolvedInstanceId_NoGiftAccepted()
    {
        GiftAttempt(12307, "NPC_Joe", 999);
        DeltaFavor("NPC_Joe", 25.0);

        _accepted.Should().BeEmpty();
    }

    // ── NPC key mismatch ─────────────────────────────────────────────────

    [Fact]
    public void DeltaWithWrongNpcKey_NoCorrelation()
    {
        AddItem(500, "Item_Potion");
        GiftAttempt(12307, "NPC_Joe", 500);
        DeltaFavor("NPC_Other", 25.0);

        _accepted.Should().BeEmpty();
    }

    [Fact]
    public void DeleteWithWrongNpcKey_NoCorrelation()
    {
        AddItem(600, "Item_Shield");
        DeltaFavor("NPC_Joe", 25.0);
        GiftAttempt(12307, "NPC_Other", 600);

        _accepted.Should().BeEmpty();
    }

    // ── Sequential gifts ─────────────────────────────────────────────────

    [Fact]
    public void MultipleGiftsInSequence_AllEmitted()
    {
        AddItem(700, "Item_Gem");
        AddItem(701, "Item_Ore");

        GiftAttempt(12307, "NPC_Joe", 700);
        DeltaFavor("NPC_Joe", 10.0);

        GiftAttempt(12307, "NPC_Joe", 701);
        DeltaFavor("NPC_Joe", 20.0);

        _accepted.Should().HaveCount(2);
        _accepted[0].ItemInternalName.Should().Be("Item_Gem");
        _accepted[0].DeltaFavor.Should().Be(10.0);
        _accepted[1].ItemInternalName.Should().Be("Item_Ore");
        _accepted[1].DeltaFavor.Should().Be(20.0);
    }

    // ── Dispose stops subscriptions ──────────────────────────────────────

    [Fact]
    public void Dispose_StopsSubscriptions()
    {
        AddItem(800, "Item_Staff");
        _correlator.Dispose();

        GiftAttempt(12307, "NPC_Joe", 800);
        DeltaFavor("NPC_Joe", 30.0);

        _accepted.Should().BeEmpty();
    }

    // ── Instance map retains after deletion ──────────────────────────────

    [Fact]
    public void InstanceMapRetains_AfterGiftCorrelation()
    {
        AddItem(900, "Item_Bow");
        GiftAttempt(12307, "NPC_Joe", 900);
        DeltaFavor("NPC_Joe", 5.0);

        _accepted.Should().ContainSingle();

        // Same instanceId re-gifted (re-acquired then gifted again)
        GiftAttempt(12307, "NPC_Joe", 900);
        DeltaFavor("NPC_Joe", 5.0);

        _accepted.Should().HaveCount(2);
    }

    // ── LiveTestBus ──────────────────────────────────────────────────────

    private sealed class LiveTestBus : IDomainEventBus
    {
        private readonly Dictionary<Type, object> _subscriptions = [];

        public IDisposable Subscribe<T>(Action<T> handler) where T : struct
        {
            if (!_subscriptions.TryGetValue(typeof(T), out var obj))
            {
                obj = new List<Action<T>>();
                _subscriptions[typeof(T)] = obj;
            }
            var list = (List<Action<T>>)obj;
            list.Add(handler);
            return new Unsubscriber<T>(list, handler);
        }

        public void Publish<T>(T domainEvent) where T : struct
        {
            if (_subscriptions.TryGetValue(typeof(T), out var obj))
            {
                var snapshot = ((List<Action<T>>)obj).ToArray();
                foreach (var handler in snapshot)
                    handler(domainEvent);
            }
        }

        private sealed class Unsubscriber<T>(List<Action<T>> list, Action<T> handler) : IDisposable
        {
            public void Dispose() => list.Remove(handler);
        }
    }
}
