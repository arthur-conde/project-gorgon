using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessMapFx</c>. Carries the absolute world
/// coordinates and the description fields. Primary consumer: Legolas (survey pin placement).
/// Free-text fields use <see cref="ReadOnlyMemory{T}"/> to defer allocation.
/// </summary>
public readonly record struct MapFxObserved(
    double X,
    double Y,
    double Z,
    ReadOnlyMemory<char> ShortName,
    ReadOnlyMemory<char> Category,
    ReadOnlyMemory<char> Message,
    LogLineMetadata Metadata);
