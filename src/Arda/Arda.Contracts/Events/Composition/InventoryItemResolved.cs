using Arda.Abstractions.Logs;

namespace Arda.Composition.Events;

/// <summary>
/// Composed event fusing Player.log inventory data (instanceId, internalName)
/// with chat observation data (displayName, count). Published when both sources
/// report the same inventory addition within temporal proximity.
/// </summary>
public readonly record struct InventoryItemResolved(
    long InstanceId,
    string InternalName,
    string DisplayName,
    int Count,
    LogLineMetadata Metadata);
