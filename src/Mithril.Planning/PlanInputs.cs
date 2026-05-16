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
