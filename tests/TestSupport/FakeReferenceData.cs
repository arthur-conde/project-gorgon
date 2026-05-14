using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;

namespace Mithril.TestSupport;

/// <summary>
/// Minimal <see cref="IReferenceDataService"/> fake for Quest source tests —
/// just enough to populate <c>Quests</c> + <c>QuestsByInternalName</c> and
/// raise <see cref="FileUpdated"/> on demand. Builds the keyed dictionaries
/// from a flat list of <see cref="Quest"/> POCOs (each carrying its own
/// envelope key in a synthetic <c>quest_&lt;ix&gt;</c> form when not provided).
/// </summary>
internal sealed class FakeReferenceData : IReferenceDataService
{
    public FakeReferenceData(IReadOnlyList<(string Key, Quest Quest)>? quests = null)
    {
        SetQuests(quests ?? []);
    }

    private Dictionary<string, Quest> _quests = new(StringComparer.Ordinal);
    private Dictionary<string, Quest> _byInternalName = new(StringComparer.Ordinal);

    public void SetQuests(IReadOnlyList<(string Key, Quest Quest)> quests)
    {
        _quests = quests.ToDictionary(p => p.Key, p => p.Quest, StringComparer.Ordinal);
        _byInternalName = quests
            .Where(p => !string.IsNullOrEmpty(p.Quest.InternalName))
            .ToDictionary(p => p.Quest.InternalName!, p => p.Quest, StringComparer.Ordinal);
    }

    public void RaiseQuestsUpdated() => FileUpdated?.Invoke(this, "quests");

    public IReadOnlyList<string> Keys { get; } = ["quests"];
    public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
    public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>();
    public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
    public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
    public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
    public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    /// <summary>Mutable in tests that need area-friendly-name resolution.</summary>
    public Dictionary<string, AreaEntry> AreasRaw { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, AreaEntry> Areas => AreasRaw;
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
    public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
    public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, Quest> Quests => _quests;
    public IReadOnlyDictionary<string, Quest> QuestsByInternalName => _byInternalName;

    /// <summary>Mutable for tests that need friendly-name resolution coverage.</summary>
    public Dictionary<string, string> StringsRaw { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Strings => StringsRaw;

    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    public event EventHandler<string>? FileUpdated;
}

internal static class QuestFactory
{
    public static (string Key, Quest Quest) Repeatable(
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

        return (key, new Quest
        {
            InternalName = internalName,
            Name = displayName,
            DisplayedLocation = location,
            Requirements = requirements.Length == 0 ? null : requirements,
            ReuseTime_Minutes = minutes,
            ReuseTime_Hours = hours,
            ReuseTime_Days = days,
        });
    }

    public static (string Key, Quest Quest) NonRepeatable(string key, string internalName, string displayName) =>
        (key, new Quest
        {
            InternalName = internalName,
            Name = displayName,
        });

    /// <summary>
    /// Synthesises a generic <see cref="QuestCompletedRecentlyRequirement"/>-shaped
    /// time gate. Most Gandalf tests don't care about the precise discriminator beyond
    /// "the quest has at least one requirement"; this returns a recognisable POCO type.
    /// </summary>
    public static QuestRequirement TimeGate(string type) => type switch
    {
        "QuestCompletedRecently" => new QuestCompletedRecentlyRequirement { T = type },
        "MinFavorLevel" => new MinFavorLevelRequirement { T = type },
        "MinSkillLevel" => new MinSkillLevelRequirement { T = type },
        _ => new UnknownQuestRequirement { T = type, DiscriminatorValue = type },
    };
}
