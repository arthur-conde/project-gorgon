using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Chat.Events;
using Arda.World.Chat.Internal;
using FluentAssertions;
using Xunit;

namespace Arda.World.Chat.Tests;

public class ChatInventoryTests
{
    private readonly SpyEventBus _bus = new();
    private readonly ChatInventory _handler;

    public ChatInventoryTests()
    {
        _handler = new ChatInventory(_bus);
    }

    private static LogLineMetadata Meta() =>
        new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    [Fact]
    public void ParsesSingleItem()
    {
        var line = "[Status] Apple added to inventory.";
        _handler.Handle(line.AsSpan(), line, Meta());

        var evt = _bus.Published<ChatInventoryObserved>().Should().ContainSingle().Which;
        evt.DisplayName.Should().Be("Apple");
        evt.Count.Should().Be(1);
    }

    [Fact]
    public void ParsesItemWithCount()
    {
        var line = "[Status] Iron Ore x3 added to inventory.";
        _handler.Handle(line.AsSpan(), line, Meta());

        var evt = _bus.Published<ChatInventoryObserved>().Should().ContainSingle().Which;
        evt.DisplayName.Should().Be("Iron Ore");
        evt.Count.Should().Be(3);
    }

    [Fact]
    public void ParsesItemWithLargeCount()
    {
        var line = "[Status] Arrow x250 added to inventory.";
        _handler.Handle(line.AsSpan(), line, Meta());

        var evt = _bus.Published<ChatInventoryObserved>().Should().ContainSingle().Which;
        evt.DisplayName.Should().Be("Arrow");
        evt.Count.Should().Be(250);
    }

    [Fact]
    public void ItemNameWithSpaces_NoCount()
    {
        var line = "[Status] Goblin Cap added to inventory.";
        _handler.Handle(line.AsSpan(), line, Meta());

        var evt = _bus.Published<ChatInventoryObserved>().Should().ContainSingle().Which;
        evt.DisplayName.Should().Be("Goblin Cap");
        evt.Count.Should().Be(1);
    }

    [Fact]
    public void NonInventoryStatusLine_IsIgnored()
    {
        var line = "[Status] The Iron Vein is 25m east and 30m north";
        _handler.Handle(line.AsSpan(), line, Meta());

        _bus.Published<ChatInventoryObserved>().Should().BeEmpty();
    }

    [Fact]
    public void EmptyMiddle_IsIgnored()
    {
        var line = "[Status]  added to inventory.";
        _handler.Handle(line.AsSpan(), line, Meta());

        _bus.Published<ChatInventoryObserved>().Should().BeEmpty();
    }
}
