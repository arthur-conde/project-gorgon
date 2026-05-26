using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when ProcessDeltaFavor reports a positive favor change during
/// an NPC interaction. Used by <see cref="Internal.GiftCorrelator"/> to
/// pair with <see cref="GiftAttempted"/> for full gift resolution.
/// </summary>
public readonly record struct DeltaFavorReceived(
    string NpcKey,
    double Delta,
    LogLineMetadata Metadata);
