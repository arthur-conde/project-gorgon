using Mithril.Shared.Reference;

namespace Mithril.Shared.Tests.Reference;

/// <summary>
/// Builder-style in-memory <see cref="IReferenceDataService"/> with seedable items,
/// powers, attributes, recipes, and profiles. Reused across the Phase 7 parser tests
/// so each file doesn't redefine its own fake.
/// </summary>
internal sealed class Phase7Fixture : IReferenceDataService
{
    public required IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; init; }
    public required IReadOnlyDictionary<long, ItemEntry> Items { get; init; }
    public required IReadOnlyDictionary<string, PowerEntry> Powers { get; init; }
    public required IReadOnlyDictionary<string, AttributeEntry> Attributes { get; init; }
    public required IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; init; }

    public IReadOnlyList<string> Keys => [];
    public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();

    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    public event EventHandler<string>? FileUpdated { add { } remove { } }

    public static Phase7Fixture Build(
        IEnumerable<ItemEntry>? items = null,
        IEnumerable<PowerEntry>? powers = null,
        IEnumerable<AttributeEntry>? attributes = null,
        IEnumerable<RecipeEntry>? recipes = null,
        IDictionary<string, IReadOnlyList<string>>? profiles = null)
    {
        var itemList = (items ?? []).ToArray();
        var powerList = (powers ?? []).ToArray();
        var attributeList = (attributes ?? []).ToArray();
        var recipeList = (recipes ?? []).ToArray();
        return new Phase7Fixture
        {
            Items = itemList.ToDictionary(i => i.Id),
            ItemsByInternalName = itemList.ToDictionary(i => i.InternalName, StringComparer.Ordinal),
            Powers = powerList.ToDictionary(p => p.InternalName, StringComparer.Ordinal),
            Attributes = attributeList.ToDictionary(a => a.Token, StringComparer.Ordinal),
            RecipesByInternalName = recipeList.ToDictionary(r => r.InternalName, StringComparer.Ordinal),
            Profiles = profiles is null
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyList<string>>(profiles, StringComparer.Ordinal),
        };
    }

    public static ItemEntry Item(
        long id,
        string internalName,
        string displayName,
        string? tsysProfile = null,
        IReadOnlyDictionary<string, int>? skillReqs = null,
        int? craftingTargetLevel = null)
        => new(Id: id, Name: displayName, InternalName: internalName, MaxStackSize: 50, IconId: 0, Keywords: [],
               SkillReqs: skillReqs,
               TSysProfile: tsysProfile,
               CraftingTargetLevel: craftingTargetLevel);

    public static PowerEntry Power(string internalName, string skill, string? suffix = null, params PowerTier[] tiers)
        => new(internalName, skill, Slots: [], Suffix: suffix, Tiers: tiers.ToDictionary(t => t.Tier));

    public static PowerTier Tier(int tier, params string[] effectDescs) => new(tier, effectDescs, 0);

    public static AttributeEntry Attribute(string token, string label, int iconId = 0)
        => new(token, label, "AsInt", "Always", null, iconId == 0 ? [] : [iconId]);

    public static RecipeEntry Recipe(string internalName, string skill, int skillLevelReq, string displayName)
        => new(
            Key: "recipe_" + internalName,
            Name: displayName,
            InternalName: internalName,
            IconId: 0,
            Skill: skill,
            SkillLevelReq: skillLevelReq,
            RewardSkill: skill,
            RewardSkillXp: 0,
            RewardSkillXpFirstTime: 0,
            RewardSkillXpDropOffLevel: null,
            RewardSkillXpDropOffPct: null,
            RewardSkillXpDropOffRate: null,
            Ingredients: [],
            ResultItems: []);
}
