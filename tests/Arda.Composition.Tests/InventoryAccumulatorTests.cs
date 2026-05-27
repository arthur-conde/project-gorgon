using Arda.Abstractions.Logs;
using Arda.Composition.Events;
using Arda.Composition.Internal;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Composition.Tests;

public class InventoryAccumulatorTests : IDisposable
{
    private readonly DomainEventBus _bus = new(NullLogger<DomainEventBus>.Instance);
    private readonly InventoryComposer _composer;

    private static readonly DateTimeOffset T0 = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    public InventoryAccumulatorTests()
    {
        _composer = new InventoryComposer(_bus, _bus);
    }

    public void Dispose() => _composer.Dispose();

    private static LogLineMetadata Meta(DateTimeOffset ts) =>
        new(Timestamp: ts, ReadOn: ts, IsReplay: false);

    // ── Add accumulation ──────────────────────────────────────────────────

    [Fact]
    public void ItemAdded_AccumulatesEntry()
    {
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));

        _composer.Items.Should().ContainKey(100);
        var item = _composer.Items[100];
        item.InternalName.Should().Be("item_sword");
        item.IsRemoved.Should().BeFalse();
        item.StackSize.Should().Be(1);
        item.FirstSeenAt.Should().Be(T0);
    }

    [Fact]
    public void DuplicateAdd_UpdatesEntry()
    {
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0.AddMinutes(5))));

        _composer.Items.Should().ContainKey(100);
        _composer.Items[100].FirstSeenAt.Should().Be(T0, "FirstSeenAt preserves earliest");
        _composer.Items[100].LastUpdatedAt.Should().Be(T0.AddMinutes(5));
    }

    [Fact]
    public void ReAdd_AfterRemoval_ClearsRemovedFlag()
    {
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));
        _bus.Publish(new InventoryItemRemoved(100, "item_sword", Meta(T0.AddMinutes(1))));
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0.AddMinutes(2))));

        _composer.Items[100].IsRemoved.Should().BeFalse();
        _composer.Items[100].RemovedAt.Should().BeNull();
    }

    // ── Update accumulation ───────────────────────────────────────────────

    [Fact]
    public void ItemUpdated_UpdatesStackSize()
    {
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));
        _bus.Publish(new InventoryItemUpdated(100, 5, 1, Meta(T0.AddSeconds(1))));

        _composer.Items[100].StackSize.Should().Be(5);
    }

    [Fact]
    public void ItemUpdated_UnknownInstance_Ignored()
    {
        _bus.Publish(new InventoryItemUpdated(999, 5, 1, Meta(T0)));

        _composer.Items.Should().NotContainKey(999);
    }

    // ── Remove (soft delete) ──────────────────────────────────────────────

    [Fact]
    public void ItemRemoved_SoftDeletes()
    {
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));
        _bus.Publish(new InventoryItemRemoved(100, "item_sword", Meta(T0.AddMinutes(1))));

        _composer.Items.Should().ContainKey(100, "soft-deleted entries are retained");
        _composer.Items[100].IsRemoved.Should().BeTrue();
        _composer.Items[100].RemovedAt.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public void ItemRemoved_UnknownInstance_CreatesEntry()
    {
        _bus.Publish(new InventoryItemRemoved(100, "item_sword", Meta(T0)));

        _composer.Items.Should().ContainKey(100);
        _composer.Items[100].IsRemoved.Should().BeTrue();
        _composer.Items[100].InternalName.Should().Be("item_sword");
    }

    // ── Resolved enrichment ───────────────────────────────────────────────

    [Fact]
    public void Resolved_EnrichesDisplayNameAndCount()
    {
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 3, Meta(T0.AddMilliseconds(100))));

        _composer.Items[100].DisplayName.Should().Be("Iron Sword");
        _composer.Items[100].StackSize.Should().Be(3);
    }

    // ── StateChanged event ────────────────────────────────────────────────

    [Fact]
    public void StateChanged_FiredOnAdd()
    {
        var fired = false;
        _composer.StateChanged += () => fired = true;

        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));

        fired.Should().BeTrue();
    }

    [Fact]
    public void StateChanged_FiredOnUpdate()
    {
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));
        var fired = false;
        _composer.StateChanged += () => fired = true;

        _bus.Publish(new InventoryItemUpdated(100, 5, 1, Meta(T0.AddSeconds(1))));

        fired.Should().BeTrue();
    }

    [Fact]
    public void StateChanged_FiredOnRemove()
    {
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));
        var fired = false;
        _composer.StateChanged += () => fired = true;

        _bus.Publish(new InventoryItemRemoved(100, "item_sword", Meta(T0.AddMinutes(1))));

        fired.Should().BeTrue();
    }

    // ── Session switch ────────────────────────────────────────────────────

    [Fact]
    public void SessionEstablished_ClearsItemsWithoutStore()
    {
        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0)));

        var session = new ComposedSession("Alice", "TestServer",
            T0.AddMinutes(5), TimeSpan.Zero, "Alice:20260526120500");
        _bus.Publish(new SessionEstablished(session, Meta(T0.AddMinutes(5))));

        _composer.Items.Should().BeEmpty("no store means fresh start on character switch");
    }

    [Fact]
    public void SameSession_DoesNotClear()
    {
        var session = new ComposedSession("Alice", "TestServer",
            T0, TimeSpan.Zero, "Alice:20260526120000");
        _bus.Publish(new SessionEstablished(session, Meta(T0)));

        _bus.Publish(new InventoryItemAdded(100, "item_sword", Meta(T0.AddSeconds(1))));

        _bus.Publish(new SessionEstablished(session, Meta(T0)));

        _composer.Items.Should().ContainKey(100);
    }
}
