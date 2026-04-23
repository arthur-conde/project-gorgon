using System.Text.Json.Serialization;
using Arwen.Domain;
using Gorgon.Shared.Reference;

namespace Arwen.State;

/// <summary>
/// Pre-per-character shape of <c>settings.json</c> — holds both global calibration *and*
/// the nested <c>FavorStates</c> keyed by character. Used **only** by the one-shot
/// <see cref="ArwenFavorFanoutMigration"/> to read the legacy blob; writes always go
/// through the current <see cref="ArwenSettings"/> shape.
/// </summary>
internal sealed class ArwenLegacySettings
{
    public Dictionary<string, Dictionary<string, NpcFavorSnapshot>> FavorStates { get; set; } = new();
    public CalibrationSettings Calibration { get; set; } = new();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ArwenLegacySettings))]
internal partial class ArwenLegacyJsonContext : JsonSerializerContext;
