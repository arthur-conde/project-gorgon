using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessEnableInteractors([], [id,])</c>.
/// Signals the close of a loot bracket. Primary consumer: Gandalf (LootBracketTracker).
/// </summary>
public readonly record struct EnableInteractorsFrame(
    long InteractorId,
    LogLineMetadata Metadata);
