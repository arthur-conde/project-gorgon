using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Arda.Composition.Internal;

/// <summary>
/// Serializable snapshot of per-NPC state, persisted per-character.
/// </summary>
internal sealed class NpcStateSnapshot : IVersionedState<NpcStateSnapshot>
{
    public int SchemaVersion { get; set; }
    public static int CurrentVersion => 1;
    public static NpcStateSnapshot Migrate(NpcStateSnapshot loaded) => loaded;

    public Dictionary<string, PersistedNpc> Npcs { get; set; } = [];

    internal sealed class PersistedNpc
    {
        public double? AbsoluteFavor { get; set; }
        public DateTimeOffset? FavorUpdatedAt { get; set; }
        public string? FavorTier { get; set; }
        public long? RemainingGold { get; set; }
        public long? GoldCap { get; set; }
        public DateTimeOffset? GoldResetsAt { get; set; }
        public DateTimeOffset? GoldUpdatedAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NpcStateSnapshot))]
internal partial class NpcStateSnapshotJsonContext : JsonSerializerContext;
