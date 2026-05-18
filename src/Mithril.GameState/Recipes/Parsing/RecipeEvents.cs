using Mithril.Shared.Logging;

namespace Mithril.GameState.Recipes.Parsing;

/// <summary>
/// One recipe's completion facet exactly as Project Gorgon emits it: the
/// numeric recipe id paired with its lifetime completion count. Both the
/// <c>ProcessLoadRecipes([ids],[counts])</c> (login / zone snapshot) and
/// <c>ProcessUpdateRecipe(id, count)</c> (per-recipe delta) log lines reduce to
/// this pair.
///
/// <para>This is the raw parse record — a faithful 1:1 of the log fields, before
/// any caveat interpretation.
/// <see cref="Mithril.GameState.Recipes.RecipeProgressSnapshot"/> is the
/// consumer-facing projection that layers the known / crafted predicates on
/// top.</para>
///
/// <list type="bullet">
///   <item><see cref="RecipeId"/> — the recipe's numeric id: the
///   <c>recipe_&lt;id&gt;</c> key in <c>recipes.json</c>. Kept as the integer
///   the log carries; a consumer (or a reference-enrichment follow-up, the
///   recipe analogue of #470) resolves it to <c>InternalName</c> / <c>Name</c>
///   / <c>Skill</c>. The model keeps the key — the project-wide
///   key→display-name convention, recipe edition. The tracker stays log-only by
///   deliberate choice (pure-int, unit-testable, no reference-data DI surface),
///   exactly mirroring the #462 skill tracker.</item>
///   <item><see cref="Completions"/> — lifetime times this recipe has been
///   crafted by this character (PG's per-recipe <c>RecipeCompletions</c>
///   ledger). Monotonic non-decreasing in practice. <c>0</c> means the recipe
///   is <b>known but never crafted</b> — a learned-only recipe; presence of the
///   id at all (in a snapshot, or via any update) is what makes it
///   <em>known</em>.</item>
/// </list>
/// </summary>
public readonly record struct RecipeCompletionRecord(int RecipeId, int Completions);

/// <summary>
/// A full known-recipe table snapshot, parsed from a
/// <c>ProcessLoadRecipes([id,…], [count,…])</c> line — two parallel,
/// equal-length integer arrays (ids ‖ completion counts). Project Gorgon emits
/// this at login and again on every zone / session transition, so it is
/// <b>authoritative and complete</b> —
/// <see cref="Mithril.GameState.Recipes.PlayerRecipeStateService"/> treats it
/// as a wholesale replace of the tracked state, never a merge. The re-fire on
/// zone changes is what lets the tracker self-heal after a mid-session start
/// (mirrors <c>ProcessLoadSkills</c> / #462 exactly — there <em>is</em> a full
/// dump, so the export's recipe half is fully eliminable, not merely
/// reducible; this resolved the open research question in #473).
/// </summary>
/// <param name="Timestamp">The source log line's timestamp (UTC — Player.log's
/// <c>[HH:MM:SS]</c> prefix is UTC).</param>
/// <param name="Recipes">Every recipe in the dump, in log order, including
/// <c>Completions==0</c> known-but-never-crafted rows — the parser does not
/// filter; interpretation is the snapshot layer's job.</param>
public sealed record RecipesSnapshotEvent(DateTime Timestamp, IReadOnlyList<RecipeCompletionRecord> Recipes)
    : LogEvent(Timestamp);

/// <summary>
/// A single recipe's state changed, parsed from a
/// <c>ProcessUpdateRecipe(id, count)</c> line. <paramref name="Recipe"/> carries
/// the new <b>absolute</b> completion count (not a delta — verified live:
/// snapshot 255 → <c>ProcessUpdateRecipe(7026, 256)</c> → next snapshot 256).
///
/// <para>The same verb conveys two distinct in-session events, disambiguated by
/// the count: <c>count == 0</c> for a previously-unknown recipe is a
/// <b>learn</b> (the discrete, recipe-specific signal that has no skill
/// analogue — verified live against trainer learns); <c>count &gt;= 1</c> is a
/// <b>craft completion</b>. PG emits no separate learn/first-time verb; the
/// classification is <see cref="Mithril.GameState.Recipes.PlayerRecipeStateService"/>'s
/// job from the prior state. A trainer learn is also accompanied by an
/// unrelated <c>ProcessTrainingScreenRemoveId(handle, slot)</c> whose slot
/// index is opaque (the offered list logs only as <c>TrainingInfo[]</c>); this
/// parser deliberately ignores it — <c>ProcessUpdateRecipe(id,0)</c> is the
/// authoritative learn signal.</para>
/// </summary>
/// <param name="Timestamp">The source log line's timestamp (UTC).</param>
/// <param name="Recipe">The single recipe id + new absolute completion count.</param>
public sealed record RecipeUpdateEvent(DateTime Timestamp, RecipeCompletionRecord Recipe)
    : LogEvent(Timestamp);
