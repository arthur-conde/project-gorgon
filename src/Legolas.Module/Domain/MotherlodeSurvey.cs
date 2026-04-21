namespace Legolas.Domain;

public sealed record MotherlodeSurvey(
    Guid Id,
    IReadOnlyList<int> DistancesByRound,
    PixelPoint? EstimatedPosition,
    bool Collected,
    int? RouteOrder)
{
    public static MotherlodeSurvey Create() =>
        new(Guid.NewGuid(), Array.Empty<int>(), null, false, null);
}

public sealed class MotherlodeSession
{
    public List<PixelPoint> PlayerPositions { get; } = new();
    public List<MotherlodeSurvey> Surveys { get; } = new();
    public int CurrentRound { get; set; }
}
