using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when one or more effects are removed from the player via
/// <c>ProcessRemoveEffects</c>. Carries the instance IDs being removed.
/// </summary>
public readonly record struct EffectsRemoved(
    IReadOnlyList<long> InstanceIds,
    LogLineMetadata Metadata);
