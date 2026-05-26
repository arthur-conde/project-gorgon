using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessVendorUpdateAvailableGold</c>. Primary consumer: Smaug.
/// </summary>
public readonly record struct VendorGoldUpdated(
    long RemainingGold,
    long GoldCap,
    LogLineMetadata Metadata);
