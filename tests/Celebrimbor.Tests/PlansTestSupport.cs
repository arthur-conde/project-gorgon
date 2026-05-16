using Celebrimbor.Services;
using Mithril.Leveling;
using Mithril.Planning;
using Mithril.Shared.Character;
using Mithril.Shared.Crafting;
using Mithril.Shared.Modules;
using Mithril.Shared.Settings;
using Mithril.Shared.Storage;

namespace Celebrimbor.Tests;

/// <summary>Shared builders for #228 PR-B/B1 manager + walker VM tests.</summary>
internal static class PlanFixtures
{
    public static PlanExecutor Executor(FakeReferenceData data, IActiveCharacterService active)
        => new(data, new CrossSkillPlanner(data, new LevelingMath(data), new RecipeExpander(data)), active);

    public static PersistedPlanPhase Phase(
        int index, string recipe, int crafts, int lvlStart, int lvlEnd, bool firstTime = false)
        => new()
        {
            PhaseIndex = index,
            RecipeInternalName = recipe,
            RecipeName = recipe,
            PredictedCrafts = crafts,
            XpPerCraft = 100,
            LevelAtStart = lvlStart,
            LevelAtEnd = lvlEnd,
            UsesFirstTimeBonus = firstTime,
        };

    public static SavedLevelingPlan Plan(
        string skill, int start, int goal, int cursor,
        IEnumerable<PersistedPlanPhase> phases,
        PlanCharacterRef? character = null,
        IEnumerable<PersistedSkillUnlock>? unlocks = null)
        => new()
        {
            Skill = skill,
            StartLevel = start,
            GoalLevel = goal,
            CurrentPhaseIndex = cursor,
            TotalCrafts = phases.Sum(p => p.PredictedCrafts),
            Phases = phases.ToList(),
            Unlocks = unlocks?.ToList() ?? [],
            Character = character,
        };
}

/// <summary>In-memory plan-library backing store for #228 PR-B/B1 VM tests.</summary>
internal sealed class InMemoryPlanLibraryStore : ISettingsStore<SavedLevelingPlanLibrary>
{
    private SavedLevelingPlanLibrary _current;
    public InMemoryPlanLibraryStore(SavedLevelingPlanLibrary? seed = null) => _current = seed ?? new();
    public int SaveCount { get; private set; }
    public string FilePath => "(memory)";
    public SavedLevelingPlanLibrary Load() => _current;
    public Task<SavedLevelingPlanLibrary> LoadAsync(CancellationToken ct = default) => Task.FromResult(_current);
    public Task SaveAsync(SavedLevelingPlanLibrary v, CancellationToken ct = default) { Save(v); return Task.CompletedTask; }
    public void Save(SavedLevelingPlanLibrary v) { _current = v; SaveCount++; }
}

/// <summary>Configurable <see cref="IActiveCharacterService"/> double.</summary>
internal sealed class FakeActiveCharacter : IActiveCharacterService
{
    public FakeActiveCharacter(CharacterSnapshot? active = null, StorageReport? storage = null)
    {
        ActiveCharacter = active;
        ActiveStorageContents = storage;
        Characters = active is null ? [] : [active];
    }

    public IReadOnlyList<CharacterSnapshot> Characters { get; set; }
    public IReadOnlyList<ReportFileInfo> StorageReports { get; } = [];
    public string? ActiveCharacterName => ActiveCharacter?.Name;
    public string? ActiveServer => ActiveCharacter?.Server;
    public CharacterSnapshot? ActiveCharacter { get; }
    public ReportFileInfo? ActiveStorageReport => null;
    public StorageReport? ActiveStorageContents { get; }
    public void SetActiveCharacter(string name, string server) { }
    public void Refresh() { }
    public event EventHandler? ActiveCharacterChanged { add { } remove { } }
    public event EventHandler? CharacterExportsChanged { add { } remove { } }
    public event EventHandler? StorageReportsChanged { add { } remove { } }
    public void Dispose() { }
}

/// <summary>Captures craft-list hand-offs so the walker's send-to-craft-list is observable.</summary>
internal sealed class RecordingCraftListImportTarget : ICraftListImportTarget
{
    public List<(IReadOnlyList<CraftListImportEntry> Recipes, string Source)> Calls { get; } = [];
    public void ImportFromLinkPayload(string base64UrlPayload) { }
    public void ImportRecipes(IReadOnlyList<CraftListImportEntry> recipes, string source)
        => Calls.Add((recipes, source));
}

internal static class StorageReportFactory
{
    /// <summary>Storage report with the given (item TypeID, stack count) holdings.</summary>
    public static StorageReport With(params (int TypeId, int Count)[] items)
        => new("Borg", "Alpha", "t", "r", 1,
            items.Select(i => new StorageItem(
                i.TypeId, "x", i.Count, 0m, "Vault", null, null, null,
                false, false, null, null, null, null, null, null, null, null, null)).ToList());
}

/// <summary>Records plan-import provenance / IDs surfaced to the manager.</summary>
internal sealed class RecordingPlanImportActivator : IModuleActivator
{
    public List<string> Activated { get; } = [];
    public bool Activate(string moduleId) { Activated.Add(moduleId); return true; }
}
