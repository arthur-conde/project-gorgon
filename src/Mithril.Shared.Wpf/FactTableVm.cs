namespace Mithril.Shared.Wpf;

/// <summary>
/// The layout the <see cref="FactTable"/> primitive renders its <see cref="FactPair"/>
/// list in. ONE control, three layouts (carry-forward #3 anti-fork) — never three
/// per-shape controls.
/// </summary>
public enum FactTableLayout
{
    /// <summary>
    /// Horizontal, dot-separated <c>Label Value · Label Value · …</c> — the
    /// recipe-header stat strip. A pair's <see cref="FactPair.Label"/> may be null
    /// (value-only segment).
    /// </summary>
    Strip,

    /// <summary>
    /// Vertical 2-column grid, one <c>Label … Value</c> per row, values right-aligned
    /// (the Bendith favor-tier capacity table).
    /// </summary>
    Grid,

    /// <summary>
    /// The degenerate case the carry-forward demands the SAME primitive degrade to: a
    /// SINGLE bare value, no label, no rows (e.g. a chest whose <c>Capacity</c> is just
    /// <c>"16 slots"</c>). <see cref="FactTableVm.Pairs"/> holds exactly one entry with
    /// a null label.
    /// </summary>
    Scalar,
}

/// <summary>
/// One label/value fact. <see cref="Label"/> is optional: null = value-only (Strip
/// segment) or the degenerate <see cref="FactTableLayout.Scalar"/>. Both members are
/// free strings — there is deliberately NO brush/pigment on the model: Fact is inert
/// per G-b (the value is NEVER gold), so the pigment lives only in the default Style,
/// not the data. A polymorphic-<c>Capacity</c> <em>range</em> (e.g. <c>"120–340"</c>)
/// is just a <see cref="Value"/> string — the model does not preclude it.
/// </summary>
/// <param name="Label">Optional fact label (<c>TextTertiaryBrush</c> in the Style); null = value-only.</param>
/// <param name="Value">The fact value (<c>TextPrimaryBrush</c> in the Style; never gold).</param>
public readonly record struct FactPair(string? Label, string Value);

/// <summary>
/// The data-carrier for the Phase-4 <see cref="FactTable"/> primitive — ONE polymorphic
/// Fact label/value group rendered in three layouts (<see cref="FactTableLayout.Strip"/>
/// ↔ <see cref="FactTableLayout.Grid"/> ↔ <see cref="FactTableLayout.Scalar"/>) from a
/// single data model.
/// <para>
/// <b>Anti-fork (carry-forward #3).</b> The recipe-header stat strip (horizontal,
/// dot-separated pairs) and the StorageVault favor-tier capacity table (vertical 2-col
/// grid) are the same data rotated; this is encoded as ONE layout-switchable primitive,
/// not two — the same rationale as the single-Link mandate subsuming
/// <c>EntityChip</c>/<c>ItemSourceChip</c>. It additionally degrades to a single flat
/// <see cref="FactTableLayout.Scalar"/> without forking into a separate control.
/// </para>
/// <para>
/// <b>Inert per G-b.</b> Every shape is Fact-inert: no border, no surface, NO gold on
/// values. There is no <c>ClickCommand</c>, no hover, zero interactivity. The model
/// carries no brush — pigment is the Style's job, so the value path cannot reference
/// gold/accent by construction.
/// </para>
/// <para>
/// <b>Polymorphic <c>Capacity</c> upstream.</b> <c>Capacity</c> is fully polymorphic in
/// the existing data — favor-tier table · flat slot count · script-atomic range ·
/// event-gated overrides (Phase 2 inventory). This primitive renders the Strip / Grid /
/// Scalar shapes; the <em>range</em> and <em>event-gated</em> shapes are the call site's
/// choice (Phase 5 decides per the carry-forward) to either map to Grid/Scalar or stay
/// plain Fact body lines. Those shapes are deliberately NOT built here — but nothing
/// precludes them: <see cref="FactPair.Value"/> is a free string, so a <c>"120–340"</c>
/// range is simply a Scalar value, and an event-gated set is just more Grid rows.
/// </para>
/// </summary>
/// <param name="Layout">Which of the three layouts to render.</param>
/// <param name="Pairs">
/// The fact pairs, in render order (preserved verbatim — the Style/strip helper never
/// reorders). For <see cref="FactTableLayout.Scalar"/> this is exactly one label-less pair.
/// </param>
/// <param name="Quiet">
/// Footer-quiet weight opt-in (the weight axis is orthogonal/P2 — see the grammar's
/// "Fact weight axis"). When true the Style switches the value run to
/// <c>AppMonoFontFamily</c> + <c>TextTertiaryBrush</c> (the footer-quiet
/// <c>InternalName</c> case). Default off — do not over-engineer weight here.
/// </param>
public sealed record FactTableVm(
    FactTableLayout Layout,
    IReadOnlyList<FactPair> Pairs,
    bool Quiet = false)
{
    /// <summary>
    /// The dot character used as the Strip segment separator. It is NOT part of any
    /// value (the grammar: "inline dot separators that are NOT part of any value") —
    /// it is injected between segments by <see cref="StripText"/> / the Style only.
    /// </summary>
    public const string StripSeparator = " · ";

    /// <summary>
    /// Convenience: the degenerate single flat scalar (e.g. <c>FactTableVm.Scalar("16 slots")</c>).
    /// Exactly one label-less pair, <see cref="FactTableLayout.Scalar"/> layout.
    /// </summary>
    public static FactTableVm Scalar(string value, bool quiet = false) =>
        new(FactTableLayout.Scalar, new[] { new FactPair(null, value) }, quiet);

    /// <summary>
    /// Convenience: the horizontal dot-separated recipe-header stat strip. Pair order
    /// is preserved; a null <see cref="FactPair.Label"/> renders value-only.
    /// </summary>
    public static FactTableVm Strip(IReadOnlyList<FactPair> pairs, bool quiet = false) =>
        new(FactTableLayout.Strip, pairs, quiet);

    /// <summary>
    /// Convenience: the vertical 2-column favor-tier capacity grid. Pair order is
    /// preserved; values are right-aligned in the Style.
    /// </summary>
    public static FactTableVm Grid(IReadOnlyList<FactPair> pairs, bool quiet = false) =>
        new(FactTableLayout.Grid, pairs, quiet);

    /// <summary>
    /// Pure helper: the rendered Strip string — <c>"Label Value · Label Value · …"</c>,
    /// with the middot injected <em>between</em> segments only (never inside a value).
    /// A null label yields a value-only segment. Factored out (mirrors
    /// <see cref="Link.ResolveClick"/> / <see cref="SetRefVm.DisplayText"/>) so
    /// separator placement is unit-testable without a visual tree; the Strip Style
    /// binds this one string instead of re-deciding separator logic in XAML.
    /// </summary>
    public string StripText =>
        string.Join(
            StripSeparator,
            Pairs.Select(p =>
                string.IsNullOrEmpty(p.Label) ? p.Value : $"{p.Label} {p.Value}"));
}
