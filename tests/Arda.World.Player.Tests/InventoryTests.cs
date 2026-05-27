using System.Collections.Frozen;
using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;
using Arda.World.Player.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Player.Tests;

public class InventoryTests
{
    private readonly SpyEventBus _bus = new();
    private readonly Inventory _inventory;

    public InventoryTests()
    {
        var pool = new InternPool(FrozenDictionary<string, string>.Empty);
        _inventory = new Inventory(_bus, pool);
    }

    private static LogLineMetadata Meta(bool isReplay = false) =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, isReplay);

    [Fact]
    public void AddItem_AddsToState_EmitsEvent()
    {
        _inventory.OnAddItem("(GoblinCap(84741837), -1, False)".AsSpan(), "", Meta());

        _inventory.Items.Should().ContainKey(84741837);
        _inventory.Items[84741837].InternalName.Should().Be("GoblinCap");
        _inventory.Items[84741837].StackSize.Should().Be(1);

        _bus.Published<InventoryItemAdded>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { InstanceId = 84741837L, InternalName = "GoblinCap" });
    }

    [Fact]
    public void AddItem_Upsert_DoesNotDuplicate()
    {
        _inventory.OnAddItem("(GoblinCap(84741837), -1, False)".AsSpan(), "", Meta());
        _inventory.OnAddItem("(GoblinCap(84741837), -1, False)".AsSpan(), "", Meta());

        _inventory.Items.Should().HaveCount(1);
        _bus.Published<InventoryItemAdded>().Should().HaveCount(2,
            "each ProcessAddItem emits an event even on upsert (zone transition re-add)");
    }

    [Fact]
    public void DeleteItem_RemovesFromState_EmitsEvent()
    {
        _inventory.OnAddItem("(ThentreeHelmet(121090869), -1, False)".AsSpan(), "", Meta());
        _bus.Clear();

        _inventory.OnDeleteItem("(121090869)".AsSpan(), "", Meta());

        _inventory.Items.Should().BeEmpty();
        _bus.Published<InventoryItemRemoved>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { InstanceId = 121090869L, InternalName = "ThentreeHelmet" });
    }

    [Fact]
    public void DeleteItem_UnknownId_IsNoOp()
    {
        _inventory.OnDeleteItem("(999999)".AsSpan(), "", Meta());

        _inventory.Items.Should().BeEmpty();
        _bus.Published<InventoryItemRemoved>().Should().BeEmpty();
    }

    [Fact]
    public void UpdateItemCode_DecodesStackSize()
    {
        _inventory.OnAddItem("(Moonstone(133343932), -1, False)".AsSpan(), "", Meta());
        _bus.Clear();

        // code = 1053077 → stackSize = (1053077 >> 16) + 1 = 16 + 1 = 17
        _inventory.OnUpdateItemCode("(133343932, 1053077, True)".AsSpan(), "", Meta());

        _inventory.Items[133343932].StackSize.Should().Be(17);
        _bus.Published<InventoryItemUpdated>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                InstanceId = 133343932L,
                NewStackSize = 17,
                PreviousStackSize = 1
            });
    }

    [Fact]
    public void UpdateItemCode_UnknownId_IsNoOp()
    {
        _inventory.OnUpdateItemCode("(999999, 1053077, True)".AsSpan(), "", Meta());

        _bus.Published<InventoryItemUpdated>().Should().BeEmpty();
    }

    [Fact]
    public void InternPool_ReusesSameInternalName_WhenSeeded()
    {
        const string goblinCap = "GoblinCap";
        var seeded = new Dictionary<string, string> { [goblinCap] = goblinCap }
            .ToFrozenDictionary(StringComparer.Ordinal);
        var pool = new InternPool(seeded);
        var inventory = new Inventory(_bus, pool);

        inventory.OnAddItem("(GoblinCap(1), -1, False)".AsSpan(), "", Meta());
        inventory.OnAddItem("(GoblinCap(2), -1, False)".AsSpan(), "", Meta());

        var name1 = inventory.Items[1].InternalName;
        var name2 = inventory.Items[2].InternalName;

        ReferenceEquals(name1, name2).Should().BeTrue();
        ReferenceEquals(name1, goblinCap).Should().BeTrue(
            "interned value should be the exact reference from the seeded dictionary");
    }

    [Fact]
    public void FullDump_Then_Delete_Then_Redump()
    {
        // Initial dump (login)
        _inventory.OnAddItem("(Sword(1), -1, False)".AsSpan(), "", Meta(isReplay: true));
        _inventory.OnAddItem("(Shield(2), -1, False)".AsSpan(), "", Meta(isReplay: true));
        _inventory.OnAddItem("(Potion(3), -1, False)".AsSpan(), "", Meta(isReplay: true));

        _inventory.Items.Should().HaveCount(3);

        // Delete one
        _inventory.OnDeleteItem("(2)".AsSpan(), "", Meta());

        _inventory.Items.Should().HaveCount(2);
        _inventory.Items.Should().NotContainKey(2);

        // Zone transition re-dump (upsert — missing item stays gone, existing re-added)
        _bus.Clear();
        _inventory.OnAddItem("(Sword(1), -1, False)".AsSpan(), "", Meta());
        _inventory.OnAddItem("(Potion(3), -1, False)".AsSpan(), "", Meta());

        _inventory.Items.Should().HaveCount(2);
        _bus.Published<InventoryItemAdded>().Should().HaveCount(2);
    }

    private sealed class SpyEventBus : IDomainEventSubscriber, IDomainEventPublisher
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
