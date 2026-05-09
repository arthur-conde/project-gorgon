using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Gandalf.Domain;

/// <summary>
/// Global set of timer definitions shared across every character. Persisted flat at
/// <c>%LocalAppData%/Mithril/Gandalf/definitions.json</c> via <see cref="Mithril.Shared.Settings.ISettingsStore{T}"/>.
/// Per-character progress lives separately in <see cref="GandalfProgress"/>.
///
/// Schema v3 collapsed the legacy v2 <c>Region</c> + <c>Map</c> pair on each
/// <see cref="GandalfTimerDef"/> into a single <c>Area</c> with an optional canonical
/// <c>AreaKey</c>. v1→v2 is handled by <c>GandalfSplitMigration</c>; v2→v3 by
/// <c>GandalfAreaFlattenMigration</c> — both pre-startup file-rewriters, so the typed
/// model never has to carry deprecated fields.
/// </summary>
public sealed class GandalfDefinitions : IVersionedState<GandalfDefinitions>
{
    public const int Version = 3;

    public static int CurrentVersion => Version;

    /// <summary>
    /// Defensive identity. The real v2→v3 migration runs in the
    /// <c>GandalfAreaFlattenMigration</c> hosted service before the typed store reads
    /// the file; anything reaching this hook should already be at the current version.
    /// If it isn't, the JSON-level migration didn't run (or didn't see the file) —
    /// stamp the version and accept the loss of legacy Region/Map values.
    /// </summary>
    public static GandalfDefinitions Migrate(GandalfDefinitions loaded)
    {
        if (loaded.SchemaVersion < Version)
        {
            loaded.SchemaVersion = Version;
        }
        return loaded;
    }

    public int SchemaVersion { get; set; } = Version;
    public List<GandalfTimerDef> Timers { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GandalfDefinitions))]
public partial class GandalfDefinitionsJsonContext : JsonSerializerContext { }
