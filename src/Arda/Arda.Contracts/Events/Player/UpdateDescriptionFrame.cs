using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessUpdateDescription</c>. Samwise is the sole consumer.
/// Free-text fields use <see cref="ReadOnlyMemory{T}"/> to defer allocation.
/// </summary>
public readonly record struct UpdateDescriptionFrame(
    long PlotId,
    ReadOnlyMemory<char> Title,
    ReadOnlyMemory<char> Description,
    ReadOnlyMemory<char> Action,
    double Scale,
    LogLineMetadata Metadata);
