using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessVendorAddItem</c>. Primary consumer: Smaug.
/// </summary>
public readonly record struct VendorItemSold(
    long Price,
    string InternalName,
    long InstanceId,
    LogLineMetadata Metadata);
