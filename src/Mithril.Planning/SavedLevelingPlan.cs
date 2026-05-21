using Mithril.Leveling;
using Mithril.Shared.Character;
using Mithril.GameReports;

namespace Mithril.Planning;

/// <summary>
/// A persisted / shareable leveling-plan artifact — distinct from the transient
/// <see cref="LevelingPlan"/> the planner computes (which it wraps in flat,
/// source-gen-friendly form). Self-contained: it embeds the <b>full</b> neutral
/// multi-skill state it was planned from, so it is reproducible and portable for
/// arbitrary input, not bound to whichever character is active.
///
/// <para>It also carries a <i>weak</i> <see cref="Character"/> reference (identity
/// only). For the common case — a plan for the player's own character — that lets
/// the app detect the embedded initial state has gone stale relative to the live
/// export (newly learned recipes, skill-ups) and offer a refresh + re-plan,
/// without ever hard-binding the artifact to a live character: a foreign or
/// absent character just means the plan runs from its embedded state.</para>
/// </summary>
public sealed class SavedLevelingPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    // ── Subject ──────────────────────────────────────────────────────────
    public string Skill { get; set; } = "";
    public int GoalLevel { get; set; }

    /// <summary>Weak (identity-only, lazily resolved, optional) character ref.</summary>
    public PlanCharacterRef? Character { get; set; }

    // ── Embedded initial state (full neutral multi-skill snapshot) ───────
    public Dictionary<string, PersistedSkillProgress> InitialSkills { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> InitialRecipeCompletions { get; set; } = new(StringComparer.Ordinal);

    // ── Plan content + walk cursor ───────────────────────────────────────
    public int StartLevel { get; set; }
    public long TotalXpNeeded { get; set; }
    public int TotalCrafts { get; set; }
    public int CurrentPhaseIndex { get; set; }
    public List<PersistedPlanPhase> Phases { get; set; } = [];
    public List<PersistedSkillUnlock> Unlocks { get; set; } = [];
    public List<PersistedSourcingEntry> Sourcing { get; set; } = [];

    // ── Accessors back into the planning/leveling domain ─────────────────
    public SkillTarget ToSkillTarget() => new(Skill, GoalLevel);

    public SkillState ToInitialSkillState()
        => new(InitialSkills.ToDictionary(
            kv => kv.Key,
            kv => new SkillProgress(kv.Value.Level, kv.Value.BonusLevels,
                                    kv.Value.XpTowardNextLevel, kv.Value.XpNeededForNextLevel),
            StringComparer.Ordinal));

    public RecipeHistory ToInitialRecipeHistory()
        => new(new Dictionary<string, int>(InitialRecipeCompletions, StringComparer.Ordinal));

    public SourcingPolicy ToSourcingPolicy()
        => Sourcing.Count == 0
            ? SourcingPolicy.CraftEverything
            : new SourcingPolicy(
                Sourcing.ToDictionary(s => s.ItemInternalName, s => s.Mode, StringComparer.Ordinal));

    /// <summary>
    /// True when this plan is for <paramref name="current"/>'s character identity
    /// and the live export's progression differs from the embedded initial state
    /// in a way that affects planning (a relevant skill changed, or a recipe was
    /// learned / completed more). Content diff — exact, no false "refresh" offers.
    /// Null/foreign character ⇒ never stale (arbitrary/hypothetical or not-this-char).
    /// </summary>
    public bool IsInitialStateStaleAgainst(CharacterSnapshot current)
    {
        if (Character is null) return false;
        if (!string.Equals(Character.Name, current.Name, StringComparison.Ordinal)
            || !string.Equals(Character.Server, current.Server, StringComparison.Ordinal))
            return false;

        foreach (var (skill, cur) in current.Skills)
        {
            if (!InitialSkills.TryGetValue(skill, out var was))
                return true; // a skill present now but absent at plan time
            if (was.Level != cur.Level
                || was.BonusLevels != cur.BonusLevels
                || was.XpTowardNextLevel != cur.XpTowardNextLevel
                || was.XpNeededForNextLevel != cur.XpNeededForNextLevel)
                return true;
        }

        foreach (var (recipe, cnt) in current.RecipeCompletions)
        {
            // Key *presence* matters, not just the count: a recipe key appearing
            // (even at 0 completions) means it was newly learned — that changes
            // what the planner can grind.
            if (!InitialRecipeCompletions.TryGetValue(recipe, out var was) || was != cnt)
                return true;
        }

        return false;
    }

    public static SavedLevelingPlan From(
        LevelingPlan plan,
        SkillTarget target,
        SkillState initialState,
        RecipeHistory initialHistory,
        SourcingPolicy sourcing,
        PlanCharacterRef? character)
        => new()
        {
            Skill = plan.Skill,
            GoalLevel = target.GoalLevel,
            Character = character,
            StartLevel = plan.StartLevel,
            TotalXpNeeded = plan.TotalXpNeeded,
            TotalCrafts = plan.TotalCrafts,
            CurrentPhaseIndex = 0,
            InitialSkills = initialState.Skills.ToDictionary(
                kv => kv.Key,
                kv => new PersistedSkillProgress
                {
                    Level = kv.Value.Level,
                    BonusLevels = kv.Value.BonusLevels,
                    XpTowardNextLevel = kv.Value.XpTowardNextLevel,
                    XpNeededForNextLevel = kv.Value.XpNeededForNextLevel,
                },
                StringComparer.Ordinal),
            InitialRecipeCompletions = new Dictionary<string, int>(initialHistory.Completions, StringComparer.Ordinal),
            Phases = plan.Phases.Select(p => new PersistedPlanPhase
            {
                PhaseIndex = p.PhaseIndex,
                RecipeKey = p.RecipeKey,
                RecipeInternalName = p.RecipeInternalName,
                RecipeName = p.RecipeName,
                IconId = p.IconId,
                PredictedCrafts = p.PredictedCrafts,
                XpPerCraft = p.XpPerCraft,
                UsesFirstTimeBonus = p.UsesFirstTimeBonus,
                FirstTimeBonusXp = p.FirstTimeBonusXp,
                LevelAtStart = p.LevelAtStart,
                LevelAtEnd = p.LevelAtEnd,
                IntermediateReuseXpPerCraft = p.IntermediateReuseXpPerCraft,
            }).ToList(),
            Unlocks = plan.Unlocks.Select(u => new PersistedSkillUnlock
            {
                AtLevel = u.AtLevel,
                RecipeInternalName = u.RecipeInternalName,
                RecipeName = u.RecipeName,
                XpPerCraftAtUnlock = u.XpPerCraftAtUnlock,
                Reason = u.Reason,
            }).ToList(),
            Sourcing = sourcing.ByItemInternalName
                .Select(kv => new PersistedSourcingEntry { ItemInternalName = kv.Key, Mode = kv.Value })
                .ToList(),
        };
}

/// <summary>Weak character reference — identity + the export timestamp at plan time.</summary>
public sealed class PlanCharacterRef
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public DateTimeOffset SnapshotExportedAt { get; set; }

    public static PlanCharacterRef FromSnapshot(CharacterSnapshot s)
        => new() { Name = s.Name, Server = s.Server, SnapshotExportedAt = s.ExportedAt };
}

public sealed class PersistedSkillProgress
{
    public int Level { get; set; }
    public int BonusLevels { get; set; }
    public long XpTowardNextLevel { get; set; }
    public long XpNeededForNextLevel { get; set; }
}

public sealed class PersistedPlanPhase
{
    public int PhaseIndex { get; set; }
    public string RecipeKey { get; set; } = "";
    public string RecipeInternalName { get; set; } = "";
    public string RecipeName { get; set; } = "";
    public int IconId { get; set; }
    public int PredictedCrafts { get; set; }
    public int XpPerCraft { get; set; }
    public bool UsesFirstTimeBonus { get; set; }
    public int FirstTimeBonusXp { get; set; }
    public int LevelAtStart { get; set; }
    public int LevelAtEnd { get; set; }
    public int IntermediateReuseXpPerCraft { get; set; }
}

public sealed class PersistedSkillUnlock
{
    public int AtLevel { get; set; }
    public string RecipeInternalName { get; set; } = "";
    public string RecipeName { get; set; } = "";
    public int XpPerCraftAtUnlock { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class PersistedSourcingEntry
{
    public string ItemInternalName { get; set; } = "";
    public SourcingMode Mode { get; set; }
}
