using System.Text.Json.Serialization;
using Mithril.Shared.Character;

namespace Mithril.GameState.Quests;

/// <summary>
/// Per-character quest journal state persisted to
/// <c>characters/{slug}/quests.json</c>. Two maps, both keyed by quest
/// InternalName:
/// <list type="bullet">
///   <item><see cref="ActiveQuests"/> — quests currently in the player's
///   journal. Replaced wholesale by <c>ProcessLoadQuests</c> on login;
///   incrementally mutated by per-quest accept / complete events.</item>
///   <item><see cref="CompletionHistory"/> — last-completed timestamp per
///   quest. Survives across sessions so cooldown anchors don't reset on
///   restart (a quest completed three days ago needs a remembered timestamp;
///   today's session log won't carry that <c>ProcessCompleteQuest</c> line).</item>
/// </list>
/// </summary>
public sealed class PlayerQuestJournalState : IVersionedState<PlayerQuestJournalState>
{
    public const int Version = 1;

    public static int CurrentVersion => Version;

    public static PlayerQuestJournalState Migrate(PlayerQuestJournalState loaded) => loaded;

    public int SchemaVersion { get; set; } = Version;

    public Dictionary<string, QuestJournalEntry> ActiveQuests { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, QuestCompletionState> CompletionHistory { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PlayerQuestJournalState))]
public partial class PlayerQuestJournalStateJsonContext : JsonSerializerContext { }
