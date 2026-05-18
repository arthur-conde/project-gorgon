namespace Mithril.GameState.Recipes;

/// <summary>How a <see cref="RecipeChange"/> was produced.</summary>
public enum RecipeChangeKind
{
    /// <summary>
    /// From a <c>ProcessUpdateRecipe(id, 0)</c> for a previously-unknown
    /// recipe — the player just <b>learned</b> it (from a trainer or a scroll)
    /// but has not crafted it. The discrete learn signal that has no skill
    /// analogue. <see cref="RecipeChange.CompletionsGained"/> is <c>0</c>.
    /// </summary>
    Learned = 0,

    /// <summary>
    /// From a <c>ProcessUpdateRecipe(id, N)</c> whose count increased — a craft
    /// completion. <see cref="RecipeChange.CompletionsGained"/> carries the
    /// increment (when a prior count was known);
    /// <see cref="RecipeChange.IsFirstCompletion"/> marks the observed
    /// <c>0 → ≥1</c> transition.
    /// </summary>
    Completed = 1,

    /// <summary>
    /// From a <c>ProcessLoadRecipes</c> full snapshot — emitted only for
    /// recipes whose projection actually differs from (or is new vs.) the prior
    /// state. A snapshot is not a gain event, so
    /// <see cref="RecipeChange.CompletionsGained"/> is <c>0</c> here even though
    /// the count may have moved (progression while Mithril wasn't tailing, or
    /// the periodic re-sync).
    /// </summary>
    SnapshotReplace = 2,
}

/// <summary>
/// A single recipe's state changed. The granular companion to
/// <see cref="IPlayerRecipeState.Subscribe"/>'s whole-snapshot push: a consumer
/// that cares <em>which</em> recipe moved (a "you learned X" / craft-count feed)
/// subscribes via <see cref="IPlayerRecipeState.SubscribeChanges"/> instead of
/// diffing snapshots itself.
///
/// <list type="bullet">
///   <item><b>Just learned</b> is <see cref="Kind"/> ==
///   <see cref="RecipeChangeKind.Learned"/>.</item>
///   <item><b>First-ever craft</b> is <see cref="IsFirstCompletion"/> — the
///   observed <c>0 → ≥1</c> transition. Only flagged when a prior count was
///   known (<see cref="Previous"/> is not null); a craft seen before any
///   baseline is a <see cref="RecipeChangeKind.Completed"/> with null
///   <see cref="Previous"/> and is deliberately <em>not</em> flagged first —
///   the honest warm-up contract (the next <c>ProcessLoadRecipes</c>
///   reconciles).</item>
/// </list>
/// </summary>
/// <param name="RecipeId">The recipe's numeric id (the map key). A consumer
/// resolves it to a display name; the model keeps the id.</param>
/// <param name="Previous">The recipe's projection before this change, or
/// <c>null</c> if it was previously unknown (first observation, or first time
/// seen after a cold start).</param>
/// <param name="Current">The recipe's projection after this change.</param>
/// <param name="CompletionsGained">Completions added on this tick for a
/// <see cref="RecipeChangeKind.Completed"/> when a prior count was known;
/// <c>0</c> for <see cref="RecipeChangeKind.Learned"/>,
/// <see cref="RecipeChangeKind.SnapshotReplace"/>, and a craft seen with no
/// baseline.</param>
/// <param name="Kind">Whether this came from a learn, a craft, or a snapshot
/// reconcile.</param>
/// <param name="Timestamp">UTC timestamp of the source log line.</param>
public readonly record struct RecipeChange(
    int RecipeId,
    RecipeProgressSnapshot? Previous,
    RecipeProgressSnapshot Current,
    int CompletionsGained,
    RecipeChangeKind Kind,
    DateTime Timestamp)
{
    /// <summary>
    /// True for the observed first-ever craft of this recipe — a
    /// <see cref="RecipeChangeKind.Completed"/> whose known prior count was
    /// <c>0</c> (covers the in-session learn-then-craft: a
    /// <see cref="RecipeChangeKind.Learned"/> at <c>0</c> followed by this
    /// <c>0 → 1</c>). Not flagged when there was no baseline
    /// (<see cref="Previous"/> null) — PG emits no first-time verb, so without
    /// a prior count we do not guess.
    /// </summary>
    public bool IsFirstCompletion =>
        Kind == RecipeChangeKind.Completed && Previous is { Completions: 0 };
}
