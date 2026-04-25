using System.Text.Json.Serialization;

namespace Gandalf.Domain;

/// <summary>
/// Global set of timer definitions shared across every character. Persisted flat at
/// <c>%LocalAppData%/Mithril/Gandalf/definitions.json</c> via <see cref="Mithril.Shared.Settings.ISettingsStore{T}"/>.
/// Per-character progress lives separately in <see cref="GandalfProgress"/>.
/// </summary>
public sealed class GandalfDefinitions
{
    public const int Version = 1;

    public int SchemaVersion { get; set; } = Version;
    public List<GandalfTimerDef> Timers { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GandalfDefinitions))]
public partial class GandalfDefinitionsJsonContext : JsonSerializerContext { }
