using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when an item is withdrawn from a storage vault. Paired with a preceding
/// <c>ProcessAddItem</c> (which adds the item to bag inventory at StackSize=1).
/// The <see cref="StackCount"/> is the authoritative stack size from the vault verb.
/// </summary>
public readonly record struct VaultWithdrawal(
    long InstanceId,
    string InternalName,
    int StackCount,
    long VaultEntityId,
    LogLineMetadata Metadata);
