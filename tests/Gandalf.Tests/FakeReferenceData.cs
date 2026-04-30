using Mithril.Shared.Reference;

namespace Gandalf.Tests;

/// <summary>
/// Minimal <see cref="IReferenceDataService"/> fake for Quest source tests —
/// just enough to populate <c>Quests</c> + <c>QuestsByInternalName</c> and
/// raise <see cref="FileUpdated"/> on demand.
/// </summary>
internal sealed class FakeReferenceData : IReferenceDataService
{
    public FakeReferenceData(IReadOnlyList<QuestEntry>? quests = null)
    {
        SetQuests(quests ?? []);
    }

    private Dictionary<string, QuestEntry> _quests = new(StringComparer.Ordinal);
    private Dictionary<string, QuestEntry> _byInternalName = new(StringComparer.Ordinal);

    public void SetQuests(IReadOnlyList<QuestEntry> quests)
    {
        _quests = quests.ToDictionary(q => q.Key, StringComparer.Ordinal);
        _byInternalName = quests.ToDictionary(q => q.InternalName, StringComparer.Ordinal);
    }

    public void RaiseQuestsUpdated() => FileUpdated?.Invoke(this, "quests");

    public IReadOnlyList<string> Keys { get; } = ["quests"];
    public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>();
    public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>();
    public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, ItemEntry>());
    public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
    public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
    public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
    public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, QuestEntry> Quests => _quests;
    public IReadOnlyDictionary<string, QuestEntry> QuestsByInternalName => _byInternalName;

    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    public event EventHandler<string>? FileUpdated;
}

internal static class QuestEntryFactory
{
    public static QuestEntry Repeatable(
        string key,
        string internalName,
        string displayName,
        TimeSpan reuse,
        string? location = null,
        params QuestRequirement[] requirements)
    {
        int? minutes = null, hours = null, days = null;
        if (reuse.Days >= 1) days = reuse.Days;
        if (reuse.Hours > 0) hours = reuse.Hours;
        if (reuse.Minutes > 0) minutes = reuse.Minutes;

        return new QuestEntry(
            Key: key,
            Name: displayName,
            InternalName: internalName,
            Description: "",
            DisplayedLocation: location,
            FavorNpc: null,
            Keywords: [],
            Objectives: [],
            Requirements: requirements,
            RequirementsToSustain: null,
            SkillRewards: [],
            ItemRewards: [],
            FavorReward: 0,
            RewardEffects: [],
            RewardLootProfile: null,
            ReuseMinutes: minutes,
            ReuseHours: hours,
            ReuseDays: days,
            PrefaceText: null,
            SuccessText: null);
    }

    public static QuestEntry NonRepeatable(string key, string internalName, string displayName) =>
        new(
            Key: key, Name: displayName, InternalName: internalName,
            Description: "", DisplayedLocation: null, FavorNpc: null,
            Keywords: [], Objectives: [], Requirements: [],
            RequirementsToSustain: null, SkillRewards: [], ItemRewards: [],
            FavorReward: 0, RewardEffects: [], RewardLootProfile: null,
            ReuseMinutes: null, ReuseHours: null, ReuseDays: null,
            PrefaceText: null, SuccessText: null);

    public static QuestRequirement TimeGate(string type) =>
        new(Type: type, Quest: null, Level: null, Npc: null, Skill: null, Keyword: null);
}
