using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when an item deletion occurs during an active NPC interaction,
/// indicating a gift attempt. Downstream consumers correlate with favor-delta
/// events to determine acceptance/rejection.
/// </summary>
public readonly record struct GiftAttempted(
    long EntityId,
    string NpcKey,
    long ItemInstanceId,
    LogLineMetadata Metadata);
