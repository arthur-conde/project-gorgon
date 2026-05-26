using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessDoDelayLoop</c>. Primary consumer: Gandalf.
/// Free-text fields use <see cref="ReadOnlyMemory{T}"/> to defer allocation.
/// </summary>
public readonly record struct DelayLoopStarted(
    double Seconds,
    ReadOnlyMemory<char> Verb,
    ReadOnlyMemory<char> Text,
    LogLineMetadata Metadata);
