using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessEndInteraction</c>. Primary consumer: Gandalf.
/// </summary>
public readonly record struct InteractionEnded(
    long EntityId,
    LogLineMetadata Metadata);
