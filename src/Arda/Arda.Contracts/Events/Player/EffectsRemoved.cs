using System.Collections.Immutable;
using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when one or more effects are removed from the player via
/// <c>ProcessRemoveEffects</c>. Carries the instance IDs being removed.
/// </summary>
public readonly record struct EffectsRemoved(
    ImmutableArray<long> InstanceIds,
    LogLineMetadata Metadata);
