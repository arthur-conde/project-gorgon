namespace Samwise.State;

public enum PlotStage
{
    Planted,
    Growing,
    Thirsty,
    NeedsFertilizer,
    Ripe,
    Harvested,
}

public sealed class Plot
{
    public required string PlotId { get; init; }
    public required string CharName { get; init; }
    public string? CropType { get; set; }
    public PlotStage Stage { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Action { get; set; } = "";
    public double Scale { get; set; }
    public DateTimeOffset PlantedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the plot entered its current paused stage (Thirsty / NeedsFertilizer); null if not paused.</summary>
    public DateTimeOffset? PausedSince { get; set; }

    /// <summary>Total time the plot has spent in paused stages across all pause intervals.</summary>
    public TimeSpan PausedDuration { get; set; }
}
