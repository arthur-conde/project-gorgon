using Gorgon.Shared.Reference;

namespace Celebrimbor.Tests;

/// <summary>Minimal in-memory IReferenceDataService for unit tests.</summary>
internal sealed class FakeReferenceData : IReferenceDataService
{
    private readonly Dictionary<long, ItemEntry> _items;
    private readonly Dictionary<string, ItemEntry> _itemsByName;
    private readonly Dictionary<string, RecipeEntry> _recipes;
    private readonly Dictionary<string, RecipeEntry> _recipesByName;

    public FakeReferenceData(IEnumerable<ItemEntry> items, IEnumerable<RecipeEntry> recipes)
    {
        _items = items.ToDictionary(i => i.Id);
        _itemsByName = _items.Values.ToDictionary(i => i.InternalName, StringComparer.Ordinal);
        _recipes = recipes.ToDictionary(r => r.Key, StringComparer.Ordinal);
        _recipesByName = _recipes.Values.ToDictionary(r => r.InternalName, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Keys => [];
    public IReadOnlyDictionary<long, ItemEntry> Items => _items;
    public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _itemsByName;
    public IReadOnlyDictionary<string, RecipeEntry> Recipes => _recipes;
    public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName => _recipesByName;
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();

    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }

    public event EventHandler<string>? FileUpdated;
    public void RaiseFileUpdated(string key) => FileUpdated?.Invoke(this, key);

    // Factory helpers for tests ──

    public static ItemEntry Item(long id, string name, params string[] keywords)
        => new(
            Id: id,
            Name: name,
            InternalName: name,
            MaxStackSize: 50,
            IconId: (int)id,
            Keywords: keywords.Select(k => new ItemKeyword(k, 0)).ToList());

    public static RecipeEntry Recipe(
        string name,
        string skill,
        int skillLevelReq,
        IReadOnlyList<RecipeItemRef> ingredients,
        IReadOnlyList<RecipeItemRef> results)
        => new(
            Key: "recipe_" + name.ToLowerInvariant(),
            Name: name,
            InternalName: name,
            IconId: 0,
            Skill: skill,
            SkillLevelReq: skillLevelReq,
            RewardSkill: skill,
            RewardSkillXp: 0,
            RewardSkillXpFirstTime: 0,
            RewardSkillXpDropOffLevel: null,
            RewardSkillXpDropOffPct: null,
            RewardSkillXpDropOffRate: null,
            Ingredients: ingredients,
            ResultItems: results);
}
