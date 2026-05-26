using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks the player's bag inventory as an instance-keyed dictionary.
/// Receives dispatches from <see cref="AddItemHandler"/>, <see cref="DeleteItemHandler"/>,
/// and <see cref="UpdateItemCodeHandler"/>.
/// </summary>
internal sealed class Inventory : IInventoryState
{
    private readonly Dictionary<long, InventoryEntry> _items = [];
    private readonly IDomainEventPublisher _bus;
    private readonly InternPool _itemPool;

    public IReadOnlyDictionary<long, InventoryEntry> Items => _items;

    public Inventory(IDomainEventPublisher bus, InternPool itemPool)
    {
        _bus = bus;
        _itemPool = itemPool;
    }

    /// <summary>
    /// Clear all state on character switch. Called when the player leaves the world
    /// (ChooseCharacter, ReconnectToServer) to prevent stale data persisting until
    /// the next character's dump arrives.
    /// </summary>
    internal void Reset() => _items.Clear();

    /// <summary>
    /// Args format: <c>(InternalName(instanceId), slot, bool)</c>
    /// Example: <c>(GoblinCap(84741837), -1, False)</c>
    /// </summary>
    internal void OnAddItem(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        // Strip outer parens: "GoblinCap(84741837), -1, False"
        var inner = SpanHelpers.StripParens(args);
        if (inner.IsEmpty)
            return;

        // Find the inner '(' separating InternalName from instanceId
        var nameEnd = inner.IndexOf('(');
        if (nameEnd <= 0)
            return;

        var nameSpan = inner[..nameEnd];
        var afterName = inner[(nameEnd + 1)..];

        // Parse instanceId — ends at the next ')'
        var idEnd = afterName.IndexOf(')');
        if (idEnd <= 0)
            return;

        if (!long.TryParse(afterName[..idEnd], out var instanceId))
            return;

        var internalName = _itemPool.InternOrAllocate(nameSpan);

        // Upsert — zone transition re-adds all items
        _items[instanceId] = new InventoryEntry(internalName, 1);
        _bus.Publish(new InventoryItemAdded(instanceId, internalName, metadata));
    }

    /// <summary>
    /// Args format: <c>(instanceId)</c>
    /// Example: <c>(135276462)</c>
    /// </summary>
    internal void OnDeleteItem(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var inner = SpanHelpers.StripParens(args);
        if (inner.IsEmpty)
            return;

        if (!long.TryParse(inner, out var instanceId))
            return;

        if (!_items.Remove(instanceId, out var entry))
            return;

        _bus.Publish(new InventoryItemRemoved(instanceId, entry.InternalName, metadata));
    }

    /// <summary>
    /// Args format: <c>(instanceId, code, bool)</c>
    /// Example: <c>(133343932, 1053077, True)</c>
    /// Stack size decode: <c>(code >> 16) + 1</c>
    /// </summary>
    internal void OnUpdateItemCode(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var inner = SpanHelpers.StripParens(args);
        if (inner.IsEmpty)
            return;

        // First arg: instanceId (up to first comma)
        var comma1 = inner.IndexOf(',');
        if (comma1 <= 0)
            return;

        if (!long.TryParse(inner[..comma1], out var instanceId))
            return;

        // Second arg: code (between first comma and second comma)
        var rest = inner[(comma1 + 1)..].TrimStart();
        var comma2 = rest.IndexOf(',');
        if (comma2 <= 0)
            return;

        if (!long.TryParse(rest[..comma2], out var code))
            return;

        var newStackSize = (int)(code >> 16) + 1;

        if (!_items.TryGetValue(instanceId, out var entry))
            return;

        var previousStackSize = entry.StackSize;
        _items[instanceId] = entry with { StackSize = newStackSize };
        _bus.Publish(new InventoryItemUpdated(instanceId, newStackSize, previousStackSize, metadata));
    }

}
