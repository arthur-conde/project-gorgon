using Gorgon.Shared.Logging;

namespace Legolas.Domain;

public abstract record GameEvent(DateTime Timestamp) : LogEvent(Timestamp);

public sealed record SurveyDetected(
    DateTime Timestamp,
    string Name,
    MetreOffset Offset) : GameEvent(Timestamp);

public sealed record ItemCollected(
    DateTime Timestamp,
    string Name,
    int Count) : GameEvent(Timestamp);

public sealed record MotherlodeDistance(
    DateTime Timestamp,
    int DistanceMetres) : GameEvent(Timestamp);

public sealed record UnknownLine(
    DateTime Timestamp,
    string RawLine) : GameEvent(Timestamp);
