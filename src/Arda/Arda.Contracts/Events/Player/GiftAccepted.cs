using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Fully-resolved gift event fusing <see cref="GiftAttempted"/> (item deleted
/// during NPC interaction) with <see cref="DeltaFavorReceived"/> (positive favor
/// change). Carries all fields Arwen needs for calibration observation recording.
/// </summary>
public readonly record struct GiftAccepted(
    long EntityId,
    string NpcKey,
    long ItemInstanceId,
    string ItemInternalName,
    double DeltaFavor,
    LogLineMetadata Metadata);
