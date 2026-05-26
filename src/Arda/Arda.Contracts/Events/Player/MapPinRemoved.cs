using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when a map pin is deleted. PG has no edit/move verb — a rename or
/// move is a Remove followed by an Add.
/// </summary>
public readonly record struct MapPinRemoved(
    double X,
    double Z,
    string Label,
    LogLineMetadata Metadata);
