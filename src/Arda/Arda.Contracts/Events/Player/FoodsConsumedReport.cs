using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for the <c>ProcessBook("Skill Info", "Foods Consumed...")</c>
/// report. Primary consumer: Pippin. The raw body is passed as-is for downstream parsing.
/// </summary>
public readonly record struct FoodsConsumedReport(
    ReadOnlyMemory<char> Body,
    LogLineMetadata Metadata);
