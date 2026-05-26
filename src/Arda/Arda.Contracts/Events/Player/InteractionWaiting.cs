using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessWaitInteraction</c>. Primary consumer: Gandalf.
/// Free-text fields use <see cref="ReadOnlyMemory{T}"/> to defer allocation.
/// </summary>
public readonly record struct InteractionWaiting(
    long EntityId,
    ReadOnlyMemory<char> Body,
    LogLineMetadata Metadata);
