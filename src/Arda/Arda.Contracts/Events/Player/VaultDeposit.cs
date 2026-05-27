using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when an item is deposited into a storage vault. Paired with a preceding
/// <c>ProcessDeleteItem</c> (which removes the item from bag inventory).
/// Downstream consumers use this to distinguish vault deposits from gifts/sales/consumption.
/// </summary>
public readonly record struct VaultDeposit(
    long InstanceId,
    string InternalName,
    long VaultEntityId,
    int SlotIndex,
    LogLineMetadata Metadata);
