using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when an item deletion during an NPC interaction is paired with a
/// positive favor change — i.e. the NPC accepted the gift. Produced by the
/// <see cref="Internal.Npc"/> handler's internal FSM which correlates
/// <c>ProcessDeleteItem</c> and <c>ProcessDeltaFavor</c> verb dispatches.
/// </summary>
public readonly record struct GiftAccepted(
    long EntityId,
    string NpcKey,
    long ItemInstanceId,
    double DeltaFavor,
    LogLineMetadata Metadata);
