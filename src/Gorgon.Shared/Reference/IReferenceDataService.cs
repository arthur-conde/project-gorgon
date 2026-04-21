namespace Gorgon.Shared.Reference;

/// <summary>
/// Manages dev-published JSON reference data from cdn.projectgorgon.com.
/// Loaded eagerly at construction from disk cache (or bundled fallback) so
/// consumers see a populated dictionary synchronously. CDN refresh runs in
/// the background and raises <see cref="FileUpdated"/> when new data lands.
/// </summary>
public interface IReferenceDataService
{
    IReadOnlyList<string> Keys { get; }

    IReadOnlyDictionary<long, ItemEntry> Items { get; }

    /// <summary>InternalName → ItemEntry lookup. Useful when the log gives an InternalName but no item id.</summary>
    IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; }

    /// <summary>recipe key (e.g. "recipe_1234") → RecipeEntry.</summary>
    IReadOnlyDictionary<string, RecipeEntry> Recipes { get; }

    /// <summary>InternalName → RecipeEntry lookup. Matches RecipeCompletions keys from character exports.</summary>
    IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; }

    /// <summary>Skill name (e.g. "Meditation") → SkillEntry.</summary>
    IReadOnlyDictionary<string, SkillEntry> Skills { get; }

    /// <summary>XP table InternalName (e.g. "TypicalNoncombatSkill") → XpTableEntry.</summary>
    IReadOnlyDictionary<string, XpTableEntry> XpTables { get; }

    /// <summary>NPC key (e.g. "NPC_Marna") → NpcEntry with gift preferences.</summary>
    IReadOnlyDictionary<string, NpcEntry> Npcs { get; }

    ReferenceFileSnapshot GetSnapshot(string key);

    Task RefreshAsync(string key, CancellationToken ct = default);

    Task RefreshAllAsync(CancellationToken ct = default);

    /// <summary>Fire-and-forget refresh of every known file. Intended for app start.</summary>
    void BeginBackgroundRefresh();

    event EventHandler<string>? FileUpdated;
}
