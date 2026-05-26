using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when one or more effects are applied to the player via
/// <c>ProcessAddEffects</c>. Carries the catalog IDs as a list.
/// </summary>
public readonly record struct EffectsAdded(
    IReadOnlyList<int> CatalogIds,
    long SourceCharId,
    LogLineMetadata Metadata);
