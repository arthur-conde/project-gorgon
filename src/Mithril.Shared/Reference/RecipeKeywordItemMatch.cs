using Mithril.Reference.Models.Items;

namespace Mithril.Shared.Reference;

/// <summary>
/// Why an item qualified as a member of the recipe-keyword-slot → matching-items index
/// (#318 slice 4, surface 3 — the recipe-detail keyword surface, retiring the synthetic
/// <c>ItemKeyword</c> #270 deep link). Mirrors the
/// <see cref="RecipeIngredientItemMatch"/> / <see cref="RecipeIngredientItemMatchReason"/>
/// shape from slice-4 surface 1 (itself a mirror of slice-1's
/// <see cref="EffectAbilityMatch"/>) so every 1:N reverse-lookup index carries the same
/// provenance-retaining structure. The popup renders membership <em>and</em> provenance
/// from this index directly — there is no second (query-string) derivation that could
/// silently diverge (the #318 invariant; see
/// <c>docs/silmarillion-tab-cookbook.md</c> §"1:1 vs 1:N — the chip-vs-popup rule").
/// <para>
/// <b>This relationship is single-reason.</b> An item is a member of a recipe keyword
/// slot's set iff, after <see cref="ItemKeywordSynthesis.Enrich"/>, the item carries
/// <em>every</em> tag in the slot's <see cref="Mithril.Reference.Models.Recipes.RecipeKeywordIngredient.ItemKeys"/>
/// list (the AND-match performed by <see cref="ItemKeywordIndex.ItemsMatching"/>). There
/// is exactly one mechanism by which an item qualifies — keyword match — with no
/// "required vs. enabled-by vs. targeted" axis like effect→abilities (slice 1). So the
/// only reason is <see cref="KeywordMatch"/>. Per the #318 <em>Discipline</em> rule a
/// single trivial reason is noise, so the popup collapses to a flat list (one section ⇒
/// <see cref="Wpf.ProvenancePopupViewModel.IsFlat"/>). The <c>[Flags]</c> enum +
/// match-record shape is retained anyway for structural parity with
/// <see cref="RecipeIngredientItemMatchReason"/> / <see cref="EffectAbilityMatchReason"/>
/// and so a future second qualification mechanic can be added as another flag without
/// reshaping the index or the popup contract.
/// </para>
/// </summary>
[System.Flags]
public enum RecipeKeywordItemMatchReason
{
    /// <summary>No reason. Never present on a real index member; the zero value.</summary>
    None = 0,

    /// <summary>
    /// The item carries (after <see cref="ItemKeywordSynthesis.Enrich"/>) every tag in
    /// the recipe keyword slot's <c>ItemKeys</c> list — i.e. it is one of the items
    /// <see cref="ItemKeywordIndex.ItemsMatching"/> returns for that slot. The sole
    /// reason in this single-reason relationship.
    /// </summary>
    KeywordMatch = 1 << 0,
}

/// <summary>
/// One member of the recipe-keyword-slot → matching-items index for a given slot
/// (keyed by the slot's <c>ItemKeys</c> '+'-joined): the qualifying <see cref="Item"/>
/// plus the <see cref="RecipeKeywordItemMatchReason"/> flags recording why it qualified.
/// The index materializes each slot's set exactly once via
/// <see cref="ItemKeywordIndex.ItemsMatching"/>, so a distinct-member count over these
/// records equals the displayed "View all N".
/// </summary>
public sealed record RecipeKeywordItemMatch(Item Item, RecipeKeywordItemMatchReason Reason);
