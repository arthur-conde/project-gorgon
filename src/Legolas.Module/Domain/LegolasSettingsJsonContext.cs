using System.Text.Json.Serialization;

namespace Legolas.Domain;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(LegolasSettings))]
public partial class LegolasSettingsJsonContext : JsonSerializerContext;
