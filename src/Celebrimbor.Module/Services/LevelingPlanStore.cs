using Mithril.Planning;
using Mithril.Shared.Settings;

namespace Celebrimbor.Services;

/// <summary>
/// Independent, id-keyed library of <see cref="SavedLevelingPlan"/> artifacts,
/// persisted module-wide to <c>%LocalAppData%/Mithril/Celebrimbor/leveling-plans.json</c>.
///
/// Deliberately NOT per-character (<c>PerCharacterView</c>): a plan can be for any
/// character or a hypothetical, and the user keeps several — each artifact carries
/// its own subject (target + embedded state + weak character ref). The store is a
/// flat collection keyed by <see cref="SavedLevelingPlan.Id"/>; "which plan am I
/// walking" is UI selection (#228 PR-B), not a storage constraint. Versioned for
/// forward-compat (#208); migrated on load.
/// </summary>
public sealed class LevelingPlanStore
{
    private readonly ISettingsStore<SavedLevelingPlanLibrary> _store;
    private readonly SavedLevelingPlanLibrary _library;

    public LevelingPlanStore(string filePath)
        : this(new JsonSettingsStore<SavedLevelingPlanLibrary>(
            filePath, SavedLevelingPlanJsonContext.Default.SavedLevelingPlanLibrary))
    {
    }

    // Test seam.
    public LevelingPlanStore(ISettingsStore<SavedLevelingPlanLibrary> store)
    {
        _store = store;
        var loaded = _store.Load();
        if (loaded.SchemaVersion != SavedLevelingPlanLibrary.CurrentVersion)
        {
            loaded = SavedLevelingPlanLibrary.Migrate(loaded);
            loaded.SchemaVersion = SavedLevelingPlanLibrary.CurrentVersion;
            _store.Save(loaded);
        }
        _library = loaded;
    }

    public IReadOnlyList<SavedLevelingPlan> All() => _library.Plans;

    public SavedLevelingPlan? Get(string id)
        => _library.Plans.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    /// <summary>Insert or replace by <see cref="SavedLevelingPlan.Id"/>, then persist.</summary>
    public void Upsert(SavedLevelingPlan plan)
    {
        var ix = _library.Plans.FindIndex(p => string.Equals(p.Id, plan.Id, StringComparison.Ordinal));
        if (ix >= 0) _library.Plans[ix] = plan;
        else _library.Plans.Add(plan);
        _store.Save(_library);
    }

    /// <summary>Remove by id; true if something was removed. Persists on change.</summary>
    public bool Delete(string id)
    {
        var removed = _library.Plans.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal)) > 0;
        if (removed) _store.Save(_library);
        return removed;
    }
}
