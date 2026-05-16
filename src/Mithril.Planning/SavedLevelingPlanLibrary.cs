using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Mithril.Planning;

/// <summary>
/// The persisted root: an independent, id-keyed library of
/// <see cref="SavedLevelingPlan"/> artifacts. Module-wide and NOT
/// per-character — a plan can be for any character or a hypothetical, and the
/// user keeps several. Versioned for forward-compat hygiene (#208); identity
/// <see cref="Migrate"/> until a breaking change lands.
/// </summary>
public sealed class SavedLevelingPlanLibrary : IVersionedState<SavedLevelingPlanLibrary>
{
    public const int Version = 1;
    public static int CurrentVersion => Version;
    public static SavedLevelingPlanLibrary Migrate(SavedLevelingPlanLibrary loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    public List<SavedLevelingPlan> Plans { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SavedLevelingPlanLibrary))]
public partial class SavedLevelingPlanJsonContext : JsonSerializerContext;
