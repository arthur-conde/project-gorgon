using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;

namespace Legolas.Tests;

/// <summary>
/// Minimal <see cref="IReferenceDataService"/> for the motherlode-map
/// predicate: only <see cref="ItemsByInternalName"/> is populated (the
/// coordinator resolves an InternalName to an item whose display Name ends
/// with "Motherlode Map"). Other members are empty/no-op.
/// </summary>
public sealed class FakeMotherlodeRefData : IReferenceDataService
{
    public FakeMotherlodeRefData(params (string InternalName, string Name)[] items)
    {
        var byName = new Dictionary<string, Item>(StringComparer.Ordinal);
        var byId = new Dictionary<long, Item>();
        long id = 1;
        foreach (var (intern, name) in items)
        {
            var it = new Item { Id = id++, InternalName = intern, Name = name };
            byName[intern] = it;
            byId[it.Id] = it;
        }
        ItemsByInternalName = byName;
        Items = byId;
    }

    public IReadOnlyList<string> Keys { get; } = [];
    public IReadOnlyDictionary<long, Item> Items { get; }
    public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; }
    public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
    public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
    public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
    public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
    public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
    public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
    public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public event EventHandler<string>? FileUpdated;
    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    private void Suppress() => FileUpdated?.Invoke(this, "");
}
