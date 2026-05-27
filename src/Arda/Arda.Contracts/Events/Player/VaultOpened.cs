using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when a storage vault UI is opened (<c>ProcessShowStorageVault</c>).
/// Marks the start of a vault interaction session.
/// </summary>
public readonly record struct VaultOpened(
    long EntityId,
    long StorageId,
    string Label,
    int SlotCount,
    LogLineMetadata Metadata);
