namespace Mithril.Planning;

/// <summary>The leveling goal. v1 is a single scalar skill target; vector
/// (multi-skill) targets are a follow-up — the math substrate already supports
/// multi-skill credit, only the planner objective stays scalar for v1.</summary>
public readonly record struct SkillTarget(string Skill, int GoalLevel);

/// <summary>How an ingredient is obtained when walking the plan.</summary>
public enum SourcingMode
{
    /// <summary>Craft it (recurse into its recipe).</summary>
    Craft,

    /// <summary>The user buys/farms it — prune its sub-DAG, don't plan its crafts.</summary>
    SupplyExternally,

    /// <summary>Ignore entirely (neither craft nor account).</summary>
    Ignore,
}

/// <summary>
/// Per-ingredient (by item <c>InternalName</c>) sourcing decisions. Resolves
/// cross-skill prereq complexity — "this recipe needs ingots, I'll buy those"
/// prunes the ingot sub-DAG. Unlisted items fall back to <see cref="Default"/>.
/// </summary>
public sealed class SourcingPolicy
{
    private readonly IReadOnlyDictionary<string, SourcingMode> _byItem;

    public SourcingPolicy(
        IReadOnlyDictionary<string, SourcingMode>? byItemInternalName = null,
        SourcingMode @default = SourcingMode.Craft)
    {
        _byItem = byItemInternalName ?? new Dictionary<string, SourcingMode>(StringComparer.Ordinal);
        Default = @default;
    }

    public static SourcingPolicy CraftEverything { get; } = new();

    public SourcingMode Default { get; }

    public IReadOnlyDictionary<string, SourcingMode> ByItemInternalName => _byItem;

    public SourcingMode For(string itemInternalName)
        => _byItem.TryGetValue(itemInternalName, out var m) ? m : Default;
}

/// <summary>
/// User-asserted future unlocks: "assume I'll have favor X / quest Y / lorebook Z
/// done by phase N." The planner does NOT pursue favor/quests/lorebooks itself —
/// non-skill unlocks are user-asserted. v1 models them as recipe
/// <c>InternalName</c>s the user asserts will be known/available. Skill-gated
/// unlocks are auto-considered by the planner and need not be listed here.
/// </summary>
public sealed class AssertedUnlocks
{
    private readonly IReadOnlySet<string> _recipes;

    public AssertedUnlocks(IEnumerable<string>? assertedRecipeInternalNames = null)
        => _recipes = assertedRecipeInternalNames is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(assertedRecipeInternalNames, StringComparer.Ordinal);

    public static AssertedUnlocks None { get; } = new();

    public bool IsAsserted(string recipeInternalName)
        => !string.IsNullOrEmpty(recipeInternalName) && _recipes.Contains(recipeInternalName);
}

/// <summary>
/// Whether the planner trusts a skill-gated recipe to be auto-learned the moment
/// its level gate is crossed. Most Project Gorgon leveling recipes are *not*
/// auto-granted — they're trained at an NPC or unlocked by a quest (~63% of the
/// skill-gated corpus as of v470). See #401.
///
/// <para><see cref="AssumeAutoLearned"/> is the v1 behavior and the default, so
/// existing callers don't regress. <see cref="RequireKnownForTrainerAndQuest"/>
/// makes a recipe carrying a <c>Training</c>/<c>Quest</c>
/// <see cref="Mithril.Shared.Reference.RecipeSource"/> pass *only* when it's
/// already known or user-asserted — it becomes the meaningful default once the
/// assertion surface (#227) ships. Item-bestow learning isn't in
/// <c>sources_recipes</c> and is deliberately not gated here.</para>
/// </summary>
public sealed class LearnabilityPolicy
{
    private LearnabilityPolicy(bool gateTrainerAndQuest) => GateTrainerAndQuest = gateTrainerAndQuest;

    /// <summary>v1 / default: any <c>SkillLevelReq &gt; 0</c> recipe is assumed
    /// learnable at its gate, regardless of how the game actually grants it.</summary>
    public static LearnabilityPolicy AssumeAutoLearned { get; } = new(false);

    /// <summary>Trainer/quest-gated recipes must be known or asserted; only
    /// genuinely source-less skill-gated recipes keep the auto-learn free pass.</summary>
    public static LearnabilityPolicy RequireKnownForTrainerAndQuest { get; } = new(true);

    /// <summary>When true, a recipe with a <c>Training</c>/<c>Quest</c> source
    /// does not satisfy availability on its skill gate alone.</summary>
    public bool GateTrainerAndQuest { get; }
}
