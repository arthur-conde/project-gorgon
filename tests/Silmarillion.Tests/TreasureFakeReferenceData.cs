using Mithril.Reference.Models.Abilities;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Npcs;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Reference;

namespace Silmarillion.Tests;

/// <summary>
/// Hand fake of <see cref="IReferenceDataService"/> for the Treasure tab tests
/// (#435). Implements only the required (non-default) interface members plus the
/// Treasure surface (<c>Powers</c> / <c>Profiles</c> / <c>ProfilesByPower</c> /
/// <c>ItemsByTSysProfile</c> / <c>RecipesByProducedItem</c> / <c>Skills</c> /
/// <c>Attributes</c>). The new reverse-view indices have interface-level
/// <c>=&gt; Empty</c> defaults, so other modules' fakes are unaffected — but this
/// fake supplies them so the detail VMs' pool/recipe cross-links resolve.
/// </summary>
public sealed class TreasureFakeReferenceData : IReferenceDataService
{
    private readonly Dictionary<string, PowerEntry> _powers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _profilesByPower = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkillEntry> _skills = new(StringComparer.Ordinal);

    public void AddPower(PowerEntry power) => _powers[power.InternalName] = power;

    public void AddSkill(string key, string displayName) =>
        _skills[key] = new SkillEntry(
            Key: key, DisplayName: displayName, Id: 0, Combat: false, XpTable: "",
            MaxBonusLevels: 0, Parents: Array.Empty<string>(),
            Rewards: new Dictionary<string, SkillRewardEntry>());

    /// <summary>Add a profile and (re)derive <see cref="ProfilesByPower"/> from it.</summary>
    public void AddProfile(string name, params string[] powerNames)
    {
        _profiles[name] = powerNames;
        foreach (var p in powerNames)
        {
            var existing = _profilesByPower.TryGetValue(p, out var l) ? new List<string>(l) : new List<string>();
            if (!existing.Contains(name)) existing.Add(name);
            _profilesByPower[p] = existing;
        }
    }

    public void RaiseFileUpdated(string key) => FileUpdated?.Invoke(this, key);

    // ── Treasure surface (#435: Pools = ProfilesByPower; the recipe leg was
    //     deferred to #214, so no ItemsByTSysProfile / RecipesByProducedItem here). ──
    public IReadOnlyDictionary<string, PowerEntry> Powers => _powers;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles => _profiles;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ProfilesByPower => _profilesByPower;
    public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;

    // ── Required interface members (minimal, mirrors AbilityKindTargetTests' fake) ──
    public IReadOnlyList<string> Keys => Array.Empty<string>();
    public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>();
    public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal);
    public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
    public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
    public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    public IReadOnlyDictionary<string, Npc> NpcsByInternalName { get; } = new Dictionary<string, Npc>();
    public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
    public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
    public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
    public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, Ability> Abilities { get; } = new Dictionary<string, Ability>();
    public IReadOnlyDictionary<string, Ability> AbilitiesByInternalName { get; } = new Dictionary<string, Ability>();

    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    public event EventHandler<string>? FileUpdated;
}
