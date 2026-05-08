using Mithril.Shared.Logging;

namespace Legolas.Domain;

public abstract record GameEvent(DateTime Timestamp) : LogEvent(Timestamp);

public sealed record SurveyDetected(
    DateTime Timestamp,
    string Name,
    MetreOffset Offset) : GameEvent(Timestamp);

public sealed record ItemCollected(
    DateTime Timestamp,
    string Name,
    int Count,
    string? SpeedBonusItem = null) : GameEvent(Timestamp);

/// <summary>
/// "[Status] X xN added to inventory." — the only chat line that carries the real
/// item count. The matching <see cref="ItemCollected"/> line that follows has no
/// count for survey collections (PG moved counts onto "added to inventory" lines).
/// LogIngestionService buffers these and drains the buffer on the next
/// <see cref="ItemCollected"/>, so non-survey adds (skinning, crafting, vendor
/// purchases) don't leak into the survey report.
/// </summary>
public sealed record ItemAddedToInventory(
    DateTime Timestamp,
    string Name,
    int Count) : GameEvent(Timestamp);

public sealed record MotherlodeDistance(
    DateTime Timestamp,
    int DistanceMetres) : GameEvent(Timestamp);

public sealed record UnknownLine(
    DateTime Timestamp,
    string RawLine) : GameEvent(Timestamp);
