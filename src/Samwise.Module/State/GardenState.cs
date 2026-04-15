using System.Text.Json.Serialization;

namespace Samwise.State;

public sealed class GardenState
{
    public Dictionary<string, Dictionary<string, PersistedPlot>> PlotsByChar { get; set; } = new();
}

public sealed class PersistedPlot
{
    public string? CropType { get; set; }
    public PlotStage Stage { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Action { get; set; } = "";
    public double Scale { get; set; }
    public DateTimeOffset PlantedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GardenState))]
public partial class GardenStateJsonContext : JsonSerializerContext { }
