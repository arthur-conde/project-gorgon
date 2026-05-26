using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Emitted once per wall-clock second of advancement observed in the log stream.
/// Deduplicated within a second so consumers see one tick per second of advancement,
/// not one per line.
/// </summary>
public readonly record struct CalendarTimeAdvanced(
    DateTimeOffset Now,
    LogLineMetadata Metadata);
