using Mithril.Planning;

namespace Celebrimbor.Domain;

/// <summary>
/// Celebrimbor-owned, JSON-source-gen-friendly snapshot of a #227 leveling plan
/// plus the walk cursor and the sourcing-policy snapshot it was planned under.
/// Kept distinct from <see cref="LevelingPlan"/> (a pure compute output whose
/// <c>SkillState</c> wraps dictionaries) so persistence stays a plain-old-data
/// concern and Mithril.Planning needs no serialization shape. Extends
/// Celebrimbor's existing craft-list store — no parallel store (#228).
/// </summary>
public sealed class PersistedPlan
{
    public string Skill { get; set; } = "";
    public int StartLevel { get; set; }
    public int GoalLevel { get; set; }
    public long TotalXpNeeded { get; set; }
    public int TotalCrafts { get; set; }

    /// <summary>Walk cursor — the phase the executor is currently on.</summary>
    public int CurrentPhaseIndex { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<PersistedPlanPhase> Phases { get; set; } = [];
    public List<PersistedSkillUnlock> Unlocks { get; set; } = [];

    /// <summary>The sourcing snapshot this plan was generated under; fed back into a re-plan.</summary>
    public List<PersistedSourcingEntry> Sourcing { get; set; } = [];

    public SkillTarget ToSkillTarget() => new(Skill, GoalLevel);

    public SourcingPolicy ToSourcingPolicy()
        => Sourcing.Count == 0
            ? SourcingPolicy.CraftEverything
            : new SourcingPolicy(
                Sourcing.ToDictionary(s => s.ItemInternalName, s => s.Mode, StringComparer.Ordinal));

    public static PersistedPlan From(LevelingPlan plan, SkillTarget target, SourcingPolicy sourcing)
        => new()
        {
            Skill = plan.Skill,
            StartLevel = plan.StartLevel,
            GoalLevel = target.GoalLevel,
            TotalXpNeeded = plan.TotalXpNeeded,
            TotalCrafts = plan.TotalCrafts,
            CurrentPhaseIndex = 0,
            CreatedAt = DateTimeOffset.Now,
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
