using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when an item's stack size changes (crafting, splitting, partial use).
/// Stack size is decoded from the item code: <c>(code >> 16) + 1</c>.
/// </summary>
public readonly record struct InventoryItemUpdated(
    long InstanceId,
    int NewStackSize,
    int PreviousStackSize,
    LogLineMetadata Metadata);
