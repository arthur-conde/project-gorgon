using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when an item is added to the player's bag inventory (login dump,
/// loot pickup, vault withdrawal, etc.).
/// </summary>
public readonly record struct InventoryItemAdded(
    long InstanceId,
    string InternalName,
    LogLineMetadata Metadata);
