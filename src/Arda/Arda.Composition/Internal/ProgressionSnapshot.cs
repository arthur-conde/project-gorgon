using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Arda.Composition.Internal;

/// <summary>
/// Serializable snapshot of the player progression state, persisted per-character.
/// Skills are keyed by interned skill key; recipes by internal name.
/// </summary>
internal sealed class ProgressionSnapshot : IVersionedState<ProgressionSnapshot>
{
    public int SchemaVersion { get; set; }
    public static int CurrentVersion => 1;
    public static ProgressionSnapshot Migrate(ProgressionSnapshot loaded) => loaded;

    public Dictionary<string, PersistedSkill> Skills { get; set; } = [];
    public Dictionary<string, int> RecipeCompletions { get; set; } = [];

    internal sealed class PersistedSkill
    {
        public required int Level { get; set; }
        public int BonusLevels { get; set; }
        public long CurrentXp { get; set; }
        public long XpNeededForNextLevel { get; set; }
        public int MaxLevel { get; set; }
        public DateTimeOffset MeasuredAt { get; set; }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProgressionSnapshot))]
internal partial class ProgressionSnapshotJsonContext : JsonSerializerContext;
