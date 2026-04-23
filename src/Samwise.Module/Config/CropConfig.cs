using System.Text.Json.Serialization;

namespace Samwise.Config;

public sealed class SlotFamily
{
    public int Max { get; set; }
}

public sealed class CropDefinition
{
    public string SlotFamily { get; set; } = "";
    public int? GrowthSeconds { get; set; }
}

public sealed class CropConfig
{
    public int SchemaVersion { get; set; } = 2;
    public Dictionary<string, SlotFamily> SlotFamilies { get; set; } = new();
    public Dictionary<string, CropDefinition> Crops { get; set; } = new();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CropConfig))]
public partial class CropConfigJsonContext : JsonSerializerContext;
