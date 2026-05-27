using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for the "You discovered a word of power!" <c>ProcessBook</c> line.
/// Primary consumer: Saruman via GameState.
/// Free-text fields use <see cref="ReadOnlyMemory{T}"/> to defer allocation.
/// </summary>
public readonly record struct WordOfPowerDiscovered(
    ReadOnlyMemory<char> Code,
    ReadOnlyMemory<char> Effect,
    ReadOnlyMemory<char> Description,
    LogLineMetadata Metadata);
