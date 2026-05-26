using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when the player sells an item to a vendor. Enriched by the
/// <see cref="Internal.Npc"/> handler with the resolved NPC key and favor
/// tier from the active vendor session. Primary consumer: Smaug.
/// </summary>
public readonly record struct VendorItemSold(
    long Price,
    string InternalName,
    long InstanceId,
    string? NpcKey,
    string? FavorTier,
    LogLineMetadata Metadata);
