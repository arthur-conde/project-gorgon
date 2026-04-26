using Mithril.Shared.Reference;

namespace Celebrimbor.Tests;

/// <summary>Minimal in-memory IReferenceDataService for unit tests.</summary>
internal sealed class FakeReferenceData : IReferenceDataService
{
    private readonly Dictionary<long, ItemEntry> _items;
    private readonly Dictionary<string, ItemEntry> _itemsByName;
    private readonly Dictionary<string, RecipeEntry> _recipes;
    private readonly Dictionary<string, RecipeEntry> _recipesByName;
    private readonly Dictionary<string, PowerEntry> _powers;
    private readonly Dictionary<string, AttributeEntry> _attributes;
    private readonly Dictionary<string, IReadOnlyList<string>> _profiles;

    public FakeReferenceData(
        IEnumerable<ItemEntry> items,
        IEnumerable<RecipeEntry> recipes,
        IEnumerable<PowerEntry>? powers = null,
        IEnumerable<AttributeEntry>? attributes = null,
        IDictionary<string, IReadOnlyList<string>>? profiles = null)
    {
        _items = items.ToDictionary(i => i.Id);
        _itemsByName = _items.Values.ToDictionary(i => i.InternalName, StringComparer.Ordinal);
        _keywordIndex = new ItemKeywordIndex(_items);
        _recipes = recipes.ToDictionary(r => r.Key, StringComparer.Ordinal);
        _recipesByName = _recipes.Values.ToDictionary(r => r.InternalName, StringComparer.Ordinal);
        _powers = (powers ?? []).ToDictionary(p => p.InternalName, StringComparer.Ordinal);
        _attributes = (attributes ?? []).ToDictionary(a => a.Token, StringComparer.Ordinal);
        _profiles = profiles is null
            ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyList<string>>(profiles, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Keys => [];
    public IReadOnlyDictionary<long, ItemEntry> Items => _items;
    public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _itemsByName;
    public ItemKeywordIndex KeywordIndex => _keywordIndex;
    private readonly ItemKeywordIndex _keywordIndex;
    public IReadOnlyDictionary<string, RecipeEntry> Recipes => _recipes;
    public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName => _recipesByName;
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
    public IReadOnlyDictionary<string, AttributeEntry> Attributes => _attributes;
    public IReadOnlyDictionary<string, PowerEntry> Powers => _powers;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles => _profiles;
    public IReadOnlyDictionary<string, QuestEntry> Quests { get; } = new Dictionary<string, QuestEntry>();
    public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName { get; } = new Dictionary<string, QuestEntry>();

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

    public static ItemEntry ItemWithProfile(long id, string name, string tsysProfile, string? equipSlot = null)
        => new(
            Id: id,
            Name: name,
            InternalName: name,
            MaxStackSize: 50,
            IconId: (int)id,
            Keywords: [],
            TSysProfile: tsysProfile,
            EquipSlot: equipSlot);

    public static PowerEntry Power(string internalName, string skill, string? suffix = null, params PowerTier[] tiers)
        => new(internalName, skill, Slots: [], Suffix: suffix, Tiers: tiers.ToDictionary(t => t.Tier));

    /// <summary>Power helper that lets a test specify <see cref="PowerEntry.Slots"/> — for the issue #8 slot-gate tests.</summary>
    public static PowerEntry Power(string internalName, string skill, string? suffix, IReadOnlyList<string> slots, params PowerTier[] tiers)
        => new(internalName, skill, Slots: slots, Suffix: suffix, Tiers: tiers.ToDictionary(t => t.Tier));

    public static PowerTier Tier(int tier, params string[] effectDescs) => new(tier, effectDescs, 0);

    /// <summary>Tier with an explicit gear-level bracket — for tests that exercise level-eligibility filters.</summary>
    public static PowerTier TierAt(int tier, int minLevel, int maxLevel, params string[] effectDescs)
        => new(tier, effectDescs, MaxLevel: maxLevel, MinLevel: minLevel);

    public static AttributeEntry Attribute(string token, string label, int iconId = 0)
        => new(token, label, "AsInt", "Always", null, iconId == 0 ? [] : [iconId]);

    public static KeyValuePair<string, IReadOnlyList<string>> Profile(string name, params string[] powers)
        => new(name, powers);

    public static RecipeKeywordIngredient Keyword(int stack, params string[] keys)
        => new(keys, Desc: null, StackSize: stack, ChanceToConsume: null);

    public static RecipeKeywordIngredient KeywordWithDesc(int stack, string desc, params string[] keys)
        => new(keys, Desc: desc, StackSize: stack, ChanceToConsume: null);

    public static RecipeEntry Recipe(
        string name,
        string skill,
        int skillLevelReq,
        IReadOnlyList<RecipeIngredient> ingredients,
        IReadOnlyList<RecipeItemRef> results,
        IReadOnlyList<string>? resultEffects = null)
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
            ResultItems: results,
            ResultEffects: resultEffects);
}
