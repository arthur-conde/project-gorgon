using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted when a map pin is placed (or bulk-replayed on login / area entry).
/// Coordinates are ground-plane (X/Z); the log's Y component is always 0 and dropped.
/// </summary>
public readonly record struct MapPinAdded(
    double X,
    double Z,
    string Label,
    int Shape,
    int Color,
    LogLineMetadata Metadata);
