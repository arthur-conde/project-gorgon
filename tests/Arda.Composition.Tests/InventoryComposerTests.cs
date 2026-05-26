using Arda.Abstractions.Logs;
using Arda.Composition.Events;
using Arda.Composition.Internal;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Player.Events;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arda.Composition.Tests;

public class InventoryComposerTests : IDisposable
{
    private readonly DomainEventBus _bus = new(NullLogger<DomainEventBus>.Instance);
    private readonly InventoryComposer _composer;
    private readonly List<InventoryItemResolved> _resolved = [];

    private static readonly DateTimeOffset BaseTime = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    public InventoryComposerTests()
    {
        _composer = new InventoryComposer(_bus);
        _bus.Subscribe<InventoryItemResolved>(e => _resolved.Add(e));
    }

    public void Dispose() => _composer.Dispose();

    private static LogLineMetadata Meta(DateTimeOffset readOn) =>
        new(Timestamp: readOn, ReadOn: readOn, IsReplay: false);

    [Fact]
    public void PlayerAdd_ThenChat_EmitsResolved()
    {
        var readOn = BaseTime;
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(readOn)));
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, Meta(readOn.AddMilliseconds(100))));

        _resolved.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                InstanceId = 1001L,
                InternalName = "item_sword",
                DisplayName = "Iron Sword",
                Count = 1,
            });
    }

    [Fact]
    public void Chat_ThenPlayerAdd_EmitsResolved()
    {
        var readOn = BaseTime;
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, Meta(readOn)));
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(readOn.AddMilliseconds(100))));

        _resolved.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                InstanceId = 1001L,
                InternalName = "item_sword",
                DisplayName = "Iron Sword",
                Count = 1,
            });
    }

    [Fact]
    public void EventsTooFarApart_NoResolution()
    {
        var readOn = BaseTime;
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(readOn)));
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, Meta(readOn.AddSeconds(3))));

        _resolved.Should().BeEmpty();
    }

    [Fact]
    public void AtExactWindowBoundary_StillMatches()
    {
        var readOn = BaseTime;
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(readOn)));
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, Meta(readOn.AddSeconds(2))));

        _resolved.Should().ContainSingle();
    }

    [Fact]
    public void MultipleAdds_MultipleChatsFIFO_CorrelateInOrder()
    {
        var readOn = BaseTime;

        _bus.Publish(new InventoryItemAdded(1, "item_a", Meta(readOn)));
        _bus.Publish(new InventoryItemAdded(2, "item_b", Meta(readOn.AddMilliseconds(50))));

        _bus.Publish(new ChatInventoryObserved("Alpha", 1, Meta(readOn.AddMilliseconds(100))));
        _bus.Publish(new ChatInventoryObserved("Beta", 2, Meta(readOn.AddMilliseconds(150))));

        _resolved.Should().HaveCount(2);
        _resolved[0].InstanceId.Should().Be(1);
        _resolved[0].DisplayName.Should().Be("Alpha");
        _resolved[1].InstanceId.Should().Be(2);
        _resolved[1].DisplayName.Should().Be("Beta");
    }

    [Fact]
    public void PendingListTrimmed_WhenExceedingMaxPending()
    {
        var readOn = BaseTime;

        for (var i = 0; i < 70; i++)
            _bus.Publish(new InventoryItemAdded(i, $"item_{i}", Meta(readOn.AddMilliseconds(i))));

        // 70 - MaxPending(64) = 6 evicted (items 0-5).
        // A chat within the window matches the oldest *surviving* item (6).
        _bus.Publish(new ChatInventoryObserved("First", 1, Meta(readOn)));

        _resolved.Should().ContainSingle()
            .Which.InstanceId.Should().Be(6, "items 0-5 were trimmed; item 6 is the oldest remaining");
    }

    [Fact]
    public void Dispose_StopsSubscriptions()
    {
        _composer.Dispose();

        var readOn = BaseTime;
        _bus.Publish(new InventoryItemAdded(1, "item_a", Meta(readOn)));
        _bus.Publish(new ChatInventoryObserved("Alpha", 1, Meta(readOn)));

        _resolved.Should().BeEmpty();
    }

    [Fact]
    public void ExpiredPendingEvents_AreRemovedByTimeTrim()
    {
        var readOn = BaseTime;

        _bus.Publish(new InventoryItemAdded(1, "item_old", Meta(readOn)));

        // Publish a newer add that triggers trimming of the old one
        _bus.Publish(new InventoryItemAdded(2, "item_new", Meta(readOn.AddSeconds(5))));

        // Now a chat arrives in range of the old event's time — but it was expired
        _bus.Publish(new ChatInventoryObserved("Old Item", 1, Meta(readOn.AddMilliseconds(500))));

        _resolved.Should().BeEmpty("the old add was expired by time-based trimming");
    }

    [Fact]
    public void ResolvedEvent_CarriesPlayerMetadata()
    {
        var readOn = BaseTime;
        var playerMeta = Meta(readOn);
        var chatMeta = Meta(readOn.AddMilliseconds(100));

        _bus.Publish(new InventoryItemAdded(1001, "item_sword", playerMeta));
        _bus.Publish(new ChatInventoryObserved("Iron Sword", 1, chatMeta));

        _resolved.Should().ContainSingle()
            .Which.Metadata.Should().Be(playerMeta);
    }
}
