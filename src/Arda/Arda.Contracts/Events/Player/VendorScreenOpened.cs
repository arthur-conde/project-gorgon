using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when a vendor screen opens. Enriched by the <see cref="Internal.Npc"/>
/// handler with the resolved NPC key from the preceding
/// <c>ProcessStartInteraction</c>. Primary consumer: Smaug.
/// </summary>
public readonly record struct VendorScreenOpened(
    long EntityId,
    string FavorTier,
    long RemainingGold,
    long GoldCap,
    DateTimeOffset GoldResetsAt,
    string? NpcKey,
    LogLineMetadata Metadata);
