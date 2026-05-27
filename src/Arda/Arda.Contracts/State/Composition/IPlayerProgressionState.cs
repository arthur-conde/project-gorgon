namespace Arda.Composition;

/// <summary>
/// A single enriched skill entry combining all fields from the Arda player world.
/// </summary>
public readonly record struct EnrichedSkill(
    string SkillKey,
    int Level,
    int BonusLevels,
    long CurrentXp,
    long XpNeededForNextLevel,
    int MaxLevel,
    bool IsCapped,
    DateTimeOffset MeasuredAt);

/// <summary>
/// Read-only view of the persistent player progression state maintained by the L4
/// <c>PlayerProgressionComposer</c>. Provides a unified skill and recipe surface
/// for multi-module consumption (Elrond, Samwise, Celebrimbor).
/// </summary>
public interface IPlayerProgressionState
{
    /// <summary>
    /// Skills keyed by interned skill type key (e.g. "Gardening", "Tanning").
    /// The reference is replaced on each mutation for safe snapshot reads.
    /// </summary>
    IReadOnlyDictionary<string, EnrichedSkill> Skills { get; }

    /// <summary>
    /// Known recipes keyed by recipe internal name, with lifetime completion counts.
    /// Normalized from the Arda pipeline's numeric recipe IDs.
    /// </summary>
    IReadOnlyDictionary<string, int> RecipeCompletions { get; }

    /// <summary>Fires after any mutation (skill update, recipe change, character switch).</summary>
    event Action? StateChanged;
}
