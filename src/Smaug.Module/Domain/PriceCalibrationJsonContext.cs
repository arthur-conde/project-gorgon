using System.Text.Json.Serialization;

namespace Smaug.Domain;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PriceCalibrationData))]
public partial class PriceCalibrationJsonContext : JsonSerializerContext;
