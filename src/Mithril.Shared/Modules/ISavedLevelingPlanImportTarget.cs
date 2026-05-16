namespace Mithril.Shared.Modules;

/// <summary>
/// Optional target that receives a leveling-plan artifact and adds it to the
/// plan library. Implemented by Celebrimbor (#228 PR-B/B1) to persist the plan,
/// activate its tab and bring the Plans area to the foreground.
///
/// <para>The plan is passed as its <b>canonical persisted JSON</b> (the same
/// shape the plan library writes — <c>SavedLevelingPlanLibrary</c>'s
/// source-generated context), not as a typed object: <c>Mithril.Planning</c>
/// depends on <c>Mithril.Shared</c>, so the shared layer cannot reference the
/// plan type without a cycle. Producers (Elrond's "Generate leveling plan",
/// #228 PR-B/B2) serialize the <c>SavedLevelingPlan</c> they built; the same
/// wire serves plan share/import (#384). Mirrors
/// <see cref="ICraftListImportTarget.ImportFromLinkPayload"/>: a single string
/// payload, all errors logged and swallowed — callers (module hand-off,
/// OS/file activation) must not throw.</para>
/// </summary>
public interface ISavedLevelingPlanImportTarget
{
    /// <summary>
    /// Deserializes <paramref name="planJson"/> (one <c>SavedLevelingPlan</c> in
    /// its persisted JSON form), upserts it into the plan library by id, and
    /// surfaces the Plans area. <paramref name="source"/> is a short provenance
    /// label for diagnostics (e.g. "Elrond", "Imported file"). Invalid or empty
    /// JSON is logged and dropped.
    /// </summary>
    void ImportPlan(string planJson, string source);
}
