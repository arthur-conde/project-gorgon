using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for generic <c>ProcessBook</c> lines that don't match
/// a more specific discriminator (Foods Consumed, Word of Power).
/// Free-text fields use <see cref="ReadOnlyMemory{T}"/> to defer allocation.
/// </summary>
public readonly record struct BookOpened(
    ReadOnlyMemory<char> Title,
    ReadOnlyMemory<char> Body,
    LogLineMetadata Metadata);
