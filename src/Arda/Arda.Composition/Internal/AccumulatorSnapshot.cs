using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Arda.Composition.Internal;

/// <summary>
/// Serializable snapshot of the inventory accumulator state, persisted per-character.
/// </summary>
internal sealed class AccumulatorSnapshot : IVersionedState<AccumulatorSnapshot>
{
    public int SchemaVersion { get; set; }
    public static int CurrentVersion => 1;
    public static AccumulatorSnapshot Migrate(AccumulatorSnapshot loaded) => loaded;

    public Dictionary<long, PersistedEntry> Entries { get; set; } = [];

    internal sealed class PersistedEntry
    {
        public required string InternalName { get; set; }
        public string? DisplayName { get; set; }
        public int StackSize { get; set; }
        public int? TypeId { get; set; }
        public bool IsRemoved { get; set; }
        public DateTimeOffset? RemovedAt { get; set; }
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastUpdatedAt { get; set; }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AccumulatorSnapshot))]
internal partial class AccumulatorSnapshotJsonContext : JsonSerializerContext;
