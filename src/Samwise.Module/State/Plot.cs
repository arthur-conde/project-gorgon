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
}
