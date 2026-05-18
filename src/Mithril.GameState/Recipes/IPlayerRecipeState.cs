using Mithril.GameState.Recipes.Parsing;

namespace Mithril.GameState.Recipes;

/// <summary>
/// Where the current <see cref="PlayerRecipeSnapshot"/> came from. Lets a
/// consumer prefer live data and surface provenance / freshness.
/// </summary>
public enum RecipeStateSource
{
    /// <summary>
    /// Nothing observed yet — the tracker has not seen a
    /// <c>ProcessLoadRecipes</c> or <c>ProcessUpdateRecipe</c> line this
    /// session. The snapshot is <see cref="PlayerRecipeSnapshot.Empty"/>.
    /// </summary>
    None = 0,

    /// <summary>
    /// Built by tailing <c>Player.log</c> (<c>ProcessLoadRecipes</c> /
    /// <c>ProcessUpdateRecipe</c>). The default and, today, only populated
    /// source. The enum is left open so a future export-fed source can be
    /// distinguished without a breaking change.
    /// </summary>
    LiveLog = 1,
}

/// <summary>
/// Consumer-facing projection of one recipe's state — the raw
/// <see cref="RecipeCompletionRecord"/> with the known / crafted distinction
/// interpreted into intent-revealing predicates so every consumer applies them
/// the same way.
///
/// <para>Membership of a recipe id in <see cref="PlayerRecipeSnapshot.Recipes"/>
/// is itself the "known" signal: a recipe is in the map iff the player has
/// learned it (it appeared in a <c>ProcessLoadRecipes</c> dump or any
/// <c>ProcessUpdateRecipe</c>). <see cref="Completions"/> then says whether it
/// has ever been crafted.</para>
/// </summary>
/// <param name="Completions">Lifetime times this recipe has been crafted by
/// this character (PG's <c>RecipeCompletions</c> ledger). <c>0</c> = known but
/// never crafted.</param>
public readonly record struct RecipeProgressSnapshot(int Completions)
{
    /// <summary>
    /// True once the recipe has been crafted at least once
    /// (<see cref="Completions"/> &gt;= 1). False for a learned-only recipe.
    /// </summary>
    public bool IsCrafted => Completions >= 1;
}

/// <summary>
/// An immutable, atomically-consistent view of the player's whole known-recipe
/// table at one instant: the per-recipe map plus when and from where it was
/// measured. Returned by value so a consumer never observes a torn read.
/// </summary>
public sealed class PlayerRecipeSnapshot
{
    /// <summary>The empty snapshot — no recipes, never measured, source
    /// <see cref="RecipeStateSource.None"/>. The cold-start value.</summary>
    public static PlayerRecipeSnapshot Empty { get; } =
        new(new Dictionary<int, RecipeProgressSnapshot>(), null, RecipeStateSource.None);

    internal PlayerRecipeSnapshot(
        IReadOnlyDictionary<int, RecipeProgressSnapshot> recipes,
        DateTime? measuredAt,
        RecipeStateSource source)
    {
        Recipes = recipes;
        MeasuredAt = measuredAt;
        Source = source;
    }

    /// <summary>
    /// Recipe id → progression. Keyed by the numeric <c>recipe_&lt;id&gt;</c>
    /// id the log carries — a consumer (or a reference-enrichment follow-up,
    /// the recipe analogue of #470) resolves the id to <c>InternalName</c> /
    /// <c>Name</c> / <c>Skill</c>. Presence of a key == the recipe is known.
    /// </summary>
    public IReadOnlyDictionary<int, RecipeProgressSnapshot> Recipes { get; }

    /// <summary>
    /// UTC timestamp of the log line that produced this snapshot, or
    /// <c>null</c> if nothing has been observed yet. (Player.log timestamps are
    /// UTC.) Surface this for freshness — a live snapshot can still be minutes
    /// old if the player has been idle in one zone.
    /// </summary>
    public DateTime? MeasuredAt { get; }

    /// <summary>Provenance of this snapshot.</summary>
    public RecipeStateSource Source { get; }

    /// <summary>True if the recipe is known (learned and/or crafted).</summary>
    public bool IsKnown(int recipeId) => Recipes.ContainsKey(recipeId);

    /// <summary>Convenience lookup for one recipe.</summary>
    public bool TryGet(int recipeId, out RecipeProgressSnapshot progress)
        => Recipes.TryGetValue(recipeId, out progress);
}

/// <summary>
/// Shared, <c>Player.log</c>-fed live view of the player's current recipe
/// state — every known recipe and its lifetime completion count, without
/// requiring a character re-export.
///
/// <para>Built by tailing two log lines (<c>ProcessLoadRecipes</c> = full
/// known-recipe snapshot at login / zone change; <c>ProcessUpdateRecipe</c> =
/// per-recipe delta — learn when count is 0, craft when &gt;=1). Because
/// <c>ProcessLoadRecipes</c> re-fires on zone transitions (verified live: a
/// full <c>[ids],[counts]</c> dump 8× in one session, structurally identical
/// to <c>ProcessLoadSkills</c>), the tracker <b>self-heals</b>: even if Mithril
/// starts tailing mid-session, the next zone change re-establishes the full
/// known set including never-crafted recipes. Until the first
/// <c>ProcessLoadRecipes</c> of the session is observed, <see cref="Current"/>
/// is <see cref="PlayerRecipeSnapshot.Empty"/> (or partial, if only isolated
/// <c>ProcessUpdateRecipe</c> lines have been seen) — this warm-up window is
/// the documented contract, not a bug. The existence of a full dump is what
/// makes the character export's recipe half fully eliminable (resolving the
/// open question in #473).</para>
///
/// <para>This is neutral shared infrastructure (it does not know about leveling
/// math, the first-time XP bonus, or any module). A consumer that needs the
/// leveling-engine <c>RecipeHistory</c> shape adapts this projection itself —
/// mirroring how Elrond's <c>SnapshotPlanInput</c> already adapts the character
/// export — so <c>Mithril.GameState</c> takes no dependency on
/// <c>Mithril.Leveling</c>.</para>
/// </summary>
public interface IPlayerRecipeState
{
    /// <summary>
    /// The current immutable snapshot. Atomically consistent: the map,
    /// <see cref="PlayerRecipeSnapshot.MeasuredAt"/>, and
    /// <see cref="PlayerRecipeSnapshot.Source"/> always belong to the same
    /// observation. Never null — <see cref="PlayerRecipeSnapshot.Empty"/>
    /// before the first observation.
    /// </summary>
    PlayerRecipeSnapshot Current { get; }

    /// <summary>
    /// Attach a handler that is invoked immediately with the
    /// <see cref="Current"/> snapshot (replay) and then again on every
    /// subsequent change. Replay + live-attach are atomic under an internal
    /// lock, so a late subscriber cannot miss the snapshot that landed between
    /// resolving the service and subscribing.
    ///
    /// <para>The handler runs synchronously under the tracker's lock — both for
    /// the replay (on the caller's thread) and for live dispatch (on the log
    /// ingestion thread). Do non-trivial work off-thread. Dispose the returned
    /// subscription to stop receiving events.</para>
    /// </summary>
    IDisposable Subscribe(Action<PlayerRecipeSnapshot> handler);

    /// <summary>
    /// Attach a handler that receives a granular <see cref="RecipeChange"/> per
    /// recipe that actually moved — the channel for consumers that care
    /// <em>which</em> recipe changed (learned / crafted feeds) rather than the
    /// whole snapshot.
    ///
    /// <para>Unlike <see cref="Subscribe"/> there is <b>no replay</b>: a
    /// <see cref="RecipeChange"/> is an event, not state. A late subscriber
    /// sees changes from the moment it attaches; for current state it reads
    /// <see cref="Current"/>. A <c>ProcessLoadRecipes</c> emits one
    /// <see cref="RecipeChangeKind.SnapshotReplace"/> only for recipes whose
    /// projection differs from (or is new vs.) the prior state — a no-op
    /// re-sync produces nothing. A <c>ProcessUpdateRecipe</c> emits one
    /// <see cref="RecipeChangeKind.Learned"/> (count 0, previously unknown) or
    /// <see cref="RecipeChangeKind.Completed"/> (count increased).</para>
    ///
    /// <para>Same threading contract as <see cref="Subscribe"/>: the handler
    /// runs synchronously under the tracker's lock on the ingestion thread —
    /// do non-trivial work off-thread. Dispose to stop receiving.</para>
    /// </summary>
    IDisposable SubscribeChanges(Action<RecipeChange> handler);
}
