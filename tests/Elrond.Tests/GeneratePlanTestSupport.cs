using Arda.Composition;
using Elrond.Services;
using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Quests;
using Mithril.Reference.Models.Recipes;
using Mithril.Shared.Character;
using Mithril.Shared.Modules;
using Mithril.Shared.Reference;
using Mithril.GameReports;

namespace Elrond.Tests;

/// <summary>Configurable active-character double for #228 PR-B/B2 VM tests.</summary>
internal sealed class FakeActiveChar : IActiveCharacterService
{
    public FakeActiveChar(CharacterSnapshot? active = null) => ActiveCharacter = active;
    public IReadOnlyList<CharacterSnapshot> Characters => ActiveCharacter is null ? [] : [ActiveCharacter];
    public IReadOnlyList<ReportFileInfo> StorageReports { get; } = [];
    public string? ActiveCharacterName => ActiveCharacter?.Name;
    public string? ActiveServer => ActiveCharacter?.Server;
    public CharacterSnapshot? ActiveCharacter { get; }
    public ReportFileInfo? ActiveStorageReport => null;
    public StorageReport? ActiveStorageContents => null;
    public void SetActiveCharacter(string name, string server) { }
    public void Refresh() { }
    public event EventHandler? ActiveCharacterChanged { add { } remove { } }
    public event EventHandler? CharacterExportsChanged { add { } remove { } }
    public event EventHandler? StorageReportsChanged { add { } remove { } }
    public void Dispose() { }
}

/// <summary>
/// Minimal IGameReportsService double — returns whichever snapshot the paired
/// FakeActiveChar exposes (#612: Elrond's snapshot input migrated to the
/// foundation reports service).
/// </summary>
internal sealed class FakeGameReports : IGameReportsService
{
    private readonly CharacterSnapshot? _active;
    public FakeGameReports(CharacterSnapshot? active = null) => _active = active;

    public IReadOnlyList<ReportFileInfo> StorageReports => [];
    public IReadOnlyList<CharacterSnapshot> CharacterSnapshots => _active is null ? [] : [_active];

    public ReportFileInfo? GetStorageReport(string? character, string? server) => null;
    public StorageReport? GetStorageContents(string? character, string? server) => null;

    public CharacterSnapshot? GetCharacterSnapshot(string? character, string? server)
    {
        if (_active is null || string.IsNullOrEmpty(character)) return null;
        if (!_active.Name.Equals(character, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrEmpty(server)
            && !_active.Server.Equals(server, StringComparison.OrdinalIgnoreCase)) return null;
        return _active;
    }

    public event EventHandler? StorageReportsChanged { add { } remove { } }
    public event EventHandler? CharacterSnapshotsChanged { add { } remove { } }
    public void Refresh() { }
    public void Dispose() { }
}

/// <summary>Configurable live-progression double for adapter/VM tests.</summary>
internal sealed class FakePlayerProgressionState : IPlayerProgressionState
{
    private Dictionary<string, EnrichedSkill> _skills = new(StringComparer.Ordinal);
    private Dictionary<string, int> _recipes = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, EnrichedSkill> Skills => _skills;
    public IReadOnlyDictionary<string, int> RecipeCompletions => _recipes;
    public event Action? StateChanged;

    public void SetSkill(string key, int level, long currentXp = 0, long xpNeeded = 100)
    {
        _skills[key] = new EnrichedSkill(key, level, 0, currentXp, xpNeeded, 50, false, DateTimeOffset.UtcNow);
        StateChanged?.Invoke();
    }

    public void SetRecipe(string internalName, int count)
    {
        _recipes[internalName] = count;
        StateChanged?.Invoke();
    }

    public void Clear()
    {
        _skills = new Dictionary<string, EnrichedSkill>(StringComparer.Ordinal);
        _recipes = new Dictionary<string, int>(StringComparer.Ordinal);
    }
}

internal sealed class FakeSessionComposer : ISessionComposer
{
    public FakeSessionComposer(ComposedSession? current = null) => Current = current;

    public ComposedSession? Current { get; set; }
#pragma warning disable CS0067
    public event Action? StateChanged;
#pragma warning restore CS0067
}

internal static class ProgressionTestSupport
{
    public static LiveProgressionAdapter AdapterFor(
        IActiveCharacterService activeChar,
        IGameReportsService? reports = null,
        FakePlayerProgressionState? progression = null,
        ISessionComposer? session = null)
    {
        reports ??= new FakeGameReports(activeChar.ActiveCharacter);
        progression ??= new FakePlayerProgressionState();
        session ??= new FakeSessionComposer(activeChar.ActiveCharacter is { } snap
            ? new ComposedSession(snap.Name, snap.Server, snap.ExportedAt, TimeSpan.Zero, $"{snap.Name}:test")
            : null);
        return new LiveProgressionAdapter(progression, reports, activeChar, session);
    }
}

/// <summary>Captures the plan JSON Elrond hands off so B2 serialization is observable.</summary>
internal sealed class RecordingPlanImportTarget : ISavedLevelingPlanImportTarget
{
    public List<(string Json, string Source)> Calls { get; } = [];
    public void ImportPlan(string planJson, string source) => Calls.Add((planJson, source));
}

/// <summary>Minimal fluent <see cref="IReferenceDataService"/> (mirrors the
/// Mithril.Planning.Tests builder) — enough for <c>CrossSkillPlanner</c> to
/// produce a real plan in B2 VM tests.</summary>
internal sealed class FakeRef : IReferenceDataService
{
    private readonly Dictionary<long, Item> _items = new();
    private readonly Dictionary<string, Item> _itemsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Recipe> _recipesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkillEntry> _skills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XpTableEntry> _xp = new(StringComparer.Ordinal);
    private int _serial;

    public FakeRef AddSkill(string key, string xpTable = "T", string? displayName = null)
    {
        _skills[key] = new SkillEntry(key, displayName ?? key, 0, false, xpTable, 0, [],
            new Dictionary<string, SkillRewardEntry>());
        return this;
    }

    public FakeRef AddXpTable(long perLevel, string name = "T", int levels = 50)
    {
        _xp[name] = new XpTableEntry(name, Enumerable.Repeat(perLevel, levels).ToList());
        return this;
    }

    public FakeRef AddItem(long id, string internalName)
    {
        var it = new Item { Id = id, Name = internalName, InternalName = internalName };
        _items[id] = it;
        _itemsByName[internalName] = it;
        return this;
    }

    public FakeRef AddRecipe(string internalName, string rewardSkill, int xp, (long id, int stack) produces)
    {
        var r = new Recipe
        {
            Key = $"recipe_{++_serial}",
            Name = internalName,
            InternalName = internalName,
            Skill = rewardSkill,
            SkillLevelReq = 0,
            RewardSkill = rewardSkill,
            RewardSkillXp = xp,
            RewardSkillXpFirstTime = 0,
            Ingredients = [],
            ResultItems = [new RecipeResultItem { ItemCode = produces.id, StackSize = produces.stack }],
        };
        _recipes[r.Key] = r;
        _recipesByName[internalName] = r;
        return this;
    }

    public IReadOnlyList<string> Keys { get; } = ["skills", "xptables", "recipes", "items"];
    public IReadOnlyDictionary<long, Item> Items => _items;
    public IReadOnlyDictionary<string, Item> ItemsByInternalName => _itemsByName;
    public ItemKeywordIndex KeywordIndex => new(new Dictionary<long, Item>());
    public IReadOnlyDictionary<string, Recipe> Recipes => _recipes;
    public IReadOnlyDictionary<string, Recipe> RecipesByInternalName => _recipesByName;
    public IReadOnlyDictionary<string, SkillEntry> Skills => _skills;
    public IReadOnlyDictionary<string, XpTableEntry> XpTables => _xp;
    public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
    public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
    public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
    public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, Quest> Quests { get; } = new Dictionary<string, Quest>();
    public IReadOnlyDictionary<string, Quest> QuestsByInternalName { get; } = new Dictionary<string, Quest>();
    public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
    public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void BeginBackgroundRefresh() { }
    public event EventHandler<string>? FileUpdated { add { } remove { } }
}
