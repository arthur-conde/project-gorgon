using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessVendorScreen</c>. Primary consumer: Smaug.
/// </summary>
public readonly record struct VendorScreenOpened(
    int EntityId,
    string FavorTier,
    long RemainingGold,
    long GoldCap,
    LogLineMetadata Metadata);
