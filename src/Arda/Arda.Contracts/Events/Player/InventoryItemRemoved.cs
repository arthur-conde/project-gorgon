using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when an item is removed from the player's bag inventory
/// (consumed, gifted, sold, destroyed, etc.).
/// </summary>
public readonly record struct InventoryItemRemoved(
    long InstanceId,
    string InternalName,
    LogLineMetadata Metadata);
