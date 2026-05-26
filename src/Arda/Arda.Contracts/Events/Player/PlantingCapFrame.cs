using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessErrorMessage(ItemUnusable, "...maximum...")</c>.
/// </summary>
public readonly record struct PlantingCapFrame(
    ReadOnlyMemory<char> SeedDisplayName,
    LogLineMetadata Metadata);
