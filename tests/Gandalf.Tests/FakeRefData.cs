using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;

namespace Gandalf.Tests;

/// <summary>
/// Minimal <see cref="IReferenceDataService"/> stub. Lets tests seed an Areas
/// dictionary; everything else is empty. Mirrors the per-module fakes scattered
/// across the test tree (e.g. Mithril.Shared.Tests' EmptyReferenceData).
/// </summary>
internal sealed class FakeRefData : IReferenceDataService
{
    public FakeRefData(params (string Key, string FriendlyName)[] areas)
    {
        var dict = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        foreach (var (key, friendly) in areas)
        {
            dict[key] = new AreaEntry(key, friendly, friendly);
        }
        Areas = dict;
    }

    public IReadOnlyList<string> Keys { get; } = [];
    public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
    public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
    public ItemKeywordIndex KeywordIndex => ItemKeywordIndex.Empty;
    public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
    public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    public IReadOnlyDictionary<string, AreaEntry> Areas { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
    public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
    public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
    public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
    /// <summary>Mutable in tests that need friendly-name resolution coverage.</summary>
    public Dictionary<string, string> StringsRaw { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Strings => StringsRaw;
    public event EventHandler<string>? FileUpdated;
    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    private void Suppress() => FileUpdated?.Invoke(this, "");
}
