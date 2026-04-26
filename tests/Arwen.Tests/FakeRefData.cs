using Mithril.Shared.Reference;

namespace Arwen.Tests;

internal sealed class FakeRefData : IReferenceDataService
{
    private readonly Dictionary<long, ItemEntry> _items;
    private readonly Dictionary<string, ItemEntry> _byName;
    private readonly Dictionary<string, NpcEntry> _npcs;

    public FakeRefData(Dictionary<long, ItemEntry> items, Dictionary<string, NpcEntry> npcs)
    {
        _items = items;
        _npcs = npcs;
        _byName = items.Values.ToDictionary(i => i.InternalName, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Keys { get; } = ["items", "npcs"];
    public IReadOnlyDictionary<long, ItemEntry> Items => _items;
    public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _byName;
    public ItemKeywordIndex KeywordIndex => new(_items);
    public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
    public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs => _npcs;
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
    public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
    public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    public event EventHandler<string>? FileUpdated { add { } remove { } }
}
