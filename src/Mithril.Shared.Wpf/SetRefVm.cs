namespace Mithril.Shared.Wpf;

/// <summary>
/// The unified data-carrier for the Phase-4 <see cref="SetRef"/> primitive — the single
/// polymorphic Set-reference VM that carries <em>both</em> shapes the G3 visual grammar
/// ratifies on one blue chassis (<c>docs/silmarillion-visual-grammar.md</c> ·
/// "Set-reference · filter / keyword / group / stacking" + Phase-4 carry-forward #4).
/// <para>
/// <b>Two shapes, one chassis (carry-forward #4):</b>
/// <list type="bullet">
///   <item><b>summary-form</b> — <see cref="MatchCount"/> non-null → renders
///     <c>"{Label} · {MatchCount} →"</c> (count + trailing arrow ride on the chip;
///     "the drawer you can pull").</item>
///   <item><b>tag-form</b> — <see cref="MatchCount"/> null → bare <see cref="Label"/>:
///     no count, no arrow, no lead glyph (the grammar's "glyph: None by default"). The
///     <em>chip shape itself</em> carries the tier.</item>
/// </list>
/// </para>
/// <para>
/// <b>Availability corollary (the never-grey-pill guarantee).</b> <see cref="IsActionable"/>
/// is <em>false</em> for an un-wired tag-form Set-ref (e.g. today's StorageVault keyword
/// tags whose filter is not yet hooked up). Per the ratified availability corollary such a
/// chip is <em>still a Set-reference in a non-activated state</em>: it MUST render on the
/// full blue Set-ref chassis and MUST NOT degrade to an inert grey Fact pill — that grey
/// pill is exactly the forbidden inverted-affordance lie #404 exists to kill.
/// <see cref="IsActionable"/> therefore changes <em>interaction only</em> (hand cursor +
/// dispatch), <b>never</b> the at-rest tier look. Mirrors the inverse-of-legacy stance
/// <see cref="LinkVm.IsNavigable"/> takes for Link's G-c degrade.
/// </para>
/// <para>
/// <b>Stacking ordinal.</b> Per the grammar's <em>Stacking semantics</em> clause: when a
/// recipe has N <em>positionally material</em> slots binding the same set (the canonical
/// "two any-Crystal ingredient slots" case where the consumer references the slot index),
/// each chip carries a small ordinal prefix (1, 2, …) in <c>TextQuaternaryBrush</c>.
/// <see cref="SlotOrdinal"/> null = positionally inert / single → no prefix.
/// </para>
/// </summary>
/// <param name="Label">The set name (e.g. <c>"Crystal"</c>, <c>"Alchemy"</c>, <c>"Potion"</c>).</param>
/// <param name="MatchCount">
/// Non-null → summary-form (<c>"{Label} · {MatchCount} →"</c>). Null → tag-form (bare label).
/// The presence of this value <em>is</em> the shape selector.
/// </param>
/// <param name="IsActionable">
/// True if the reveal/filter action is wired (click dispatches, hand cursor). False = not
/// yet wired: <em>still a Set-ref on the blue chassis</em> (availability corollary) — only
/// interaction differs, never the at-rest look.
/// </param>
/// <param name="SlotOrdinal">
/// Stacking ordinal prefix for positionally-material stacked slots (1, 2, …). Null =
/// positionally inert / single → no prefix.
/// </param>
public sealed record SetRefVm(
    string Label,
    int? MatchCount = null,
    bool IsActionable = true,
    int? SlotOrdinal = null)
{
    /// <summary>
    /// True when <see cref="MatchCount"/> is non-null → render the summary-form
    /// (<c>"{Label} · {MatchCount} →"</c>). False → tag-form (bare <see cref="Label"/>).
    /// This is the pure shape selector; the template binds its count/arrow visibility to
    /// it so the two shapes never fork into two controls (anti-fork rationale, carry-fwd #4).
    /// </summary>
    public bool IsSummaryForm => MatchCount is not null;

    /// <summary>
    /// True when <see cref="SlotOrdinal"/> is set → the leading ordinal prefix renders.
    /// A VM bool (not a converter) precisely so the template uses the object-safe
    /// visibility path without a bespoke <c>int?</c>→Visibility converter — and to dodge
    /// the string-only <c>NullOrEmptyToVis</c> repo footgun entirely.
    /// </summary>
    public bool HasOrdinal => SlotOrdinal is not null;

    /// <summary>
    /// The body text for the chip, with the shape baked in: tag-form is the bare label;
    /// summary-form appends <c> · {count} →</c>. Factored onto the VM (pure, no visual
    /// tree) so the summary-vs-tag selection is unit-testable and the template binds one
    /// string instead of re-deciding the shape in XAML.
    /// </summary>
    public string DisplayText =>
        IsSummaryForm
            // Ratified summary-form (grammar doc "Crystal · 150 matches →"). The spec
            // only exemplified the plural; "1 matches" reads as a bug, so the noun is
            // count-aware — sensible reading of the spec, flagged not silently decided.
            ? $"{Label} · {MatchCount} {(MatchCount == 1 ? "match" : "matches")} →"
            : Label;

    /// <summary>The leading ordinal prefix glyph (e.g. <c>"1"</c>), or empty when none.</summary>
    public string OrdinalText => HasOrdinal ? SlotOrdinal!.Value.ToString() : string.Empty;
}
