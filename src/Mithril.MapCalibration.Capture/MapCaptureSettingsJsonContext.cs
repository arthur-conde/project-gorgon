using System.Text.Json.Serialization;

namespace Mithril.MapCalibration.Capture;

/// <summary>
/// Source-generated serialization context for <see cref="MapCaptureSettings"/>
/// (CLAUDE.md: no reflection serialization). <see cref="CaptureRect"/> is
/// reachable as a nested member, so the generator emits its converter too.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MapCaptureSettings))]
public partial class MapCaptureSettingsJsonContext : JsonSerializerContext;
