using System.Collections.Generic;
using System.Text.Json.Serialization;
using Mithril.MapCalibration.Detection.Internal;

namespace Mithril.MapCalibration.Internal;

/// <summary>
/// Source-generated <c>JsonSerializerContext</c> for the bundled baseline file
/// and the per-user refinement store. Project convention requires reflection-free
/// serialization (CLAUDE.md, "Settings classes" bullet).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(BundledBaselineFile))]
[JsonSerializable(typeof(UserRefinementFile))]
[JsonSerializable(typeof(Dictionary<string, AreaCalibration>))]
[JsonSerializable(typeof(AreaCalibration))]
[JsonSerializable(typeof(IconTemplateManifest))]
internal sealed partial class MapCalibrationJsonContext : JsonSerializerContext;

/// <summary>On-disk shape for the embedded baseline JSON resource.</summary>
internal sealed record BundledBaselineFile(
    int SchemaVersion,
    Dictionary<string, AreaCalibration> Anchors);

/// <summary>On-disk shape for the per-user refinement store JSON file.</summary>
internal sealed record UserRefinementFile(
    int SchemaVersion,
    Dictionary<string, AreaCalibration> Calibrations);
