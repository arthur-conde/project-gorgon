using Mithril.Shared.Reference;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Type-code for a <see cref="Link"/>'s 12px lead Lucide glyph. Maps to a
/// <c>PackIconLucideKind</c> by the <see cref="Link"/> control template's
/// converter. <see cref="None"/> renders no glyph (the name stands alone).
/// </summary>
public enum LinkGlyph
{
    /// <summary>No lead glyph.</summary>
    None,
    Skill,
    Recipe,
    Ingredient,
    Npc,
    Location,
    Item,
    CombatAbility,
}

/// <summary>
/// The unified data-carrier for the Phase-4 <see cref="Link"/> primitive — the single
/// VM that subsumes both <see cref="EntityChipVm"/> (cross-entity navigation chip) and
/// <see cref="ItemSourceChipVm"/> (item/recipe source row with a provenance suffix).
/// <para>
/// Per the G3 visual grammar (<c>docs/silmarillion-visual-grammar.md</c> · "Link ·
/// navigates · V2"): a Link is <c>&lt;lead glyph&gt; &lt;gold name&gt;</c> plus three
/// optional bits — a provenance suffix, a kind label, and the availability/degrade flag
/// (<see cref="IsNavigable"/>). Crucially, per G-c, <see cref="IsNavigable"/> is the
/// <em>opposite</em> of EntityChip's legacy "grey it out" degrade: a non-navigable Link
/// looks <em>identical</em> at rest (zero shipping-schedule leak); the difference shows
/// only on interaction (copy-to-clipboard instead of navigate).
/// </para>
/// Phase 5 migrates call sites; the static <see cref="From(EntityChipVm)"/> /
/// <see cref="From(ItemSourceChipVm)"/> adapters make that mechanical.
/// </summary>
/// <param name="DisplayName">The human-readable name (gold, body weight).</param>
/// <param name="Glyph">The 12px lead glyph type-code; <see cref="LinkGlyph.None"/> for no glyph.</param>
/// <param name="Reference">
/// The navigation target, passed to the host's <see cref="Link.ClickCommand"/> on a
/// navigable click. Mirrors <see cref="EntityChipVm.Reference"/>'s type.
/// </param>
/// <param name="IsNavigable">
/// True if the target tab is shipped (click navigates). False = target not yet
/// browsable: <em>still a Link</em> (G-c) — identical at rest, click copies the name.
/// </param>
/// <param name="ProvenanceSuffix">
/// Optional ItemSourceChip-style trailing provenance (e.g. <c>"from Distil Brine"</c>);
/// italic, quaternary, ~10pt. Null everywhere it doesn't apply.
/// </param>
/// <param name="KindLabel">
/// Optional, rare trailing kind disambiguator (e.g. <c>"skill"</c>) when icon+name
/// aren't enough. Same trailing slot as <see cref="ProvenanceSuffix"/>.
/// </param>
public sealed record LinkVm(
    string DisplayName,
    LinkGlyph Glyph,
    EntityRef? Reference,
    bool IsNavigable,
    string? ProvenanceSuffix = null,
    string? KindLabel = null)
{
    /// <summary>
    /// Maps an <see cref="EntityRef"/>'s <see cref="EntityKind"/> to the Link lead-glyph
    /// type-code. Unknown / non-entity kinds (keyword-filter pivots, Effect envelopes,
    /// Quest/Area/etc. that have no grammar-assigned glyph) fall back to
    /// <see cref="LinkGlyph.None"/> by design — the grammar only type-codes the seven
    /// concrete entity families it enumerates.
    /// </summary>
    public static LinkGlyph GlyphFor(EntityKind kind) => kind switch
    {
        EntityKind.Skill => LinkGlyph.Skill,
        EntityKind.Recipe => LinkGlyph.Recipe,
        EntityKind.Npc => LinkGlyph.Npc,
        EntityKind.Area => LinkGlyph.Location,
        EntityKind.Item => LinkGlyph.Item,
        EntityKind.ItemByKeyword => LinkGlyph.Item,
        EntityKind.Ability => LinkGlyph.CombatAbility,
        _ => LinkGlyph.None,
    };

    /// <summary>
    /// Adapts a legacy <see cref="EntityChipVm"/> into the unified Link VM. The glyph is
    /// derived from <see cref="EntityChipVm.Reference"/>'s kind (the legacy
    /// <c>IconId</c> is dropped — Link is glyph-coded, not icon-imaged, per the grammar).
    /// </summary>
    public static LinkVm From(EntityChipVm chip) => new(
        chip.DisplayName,
        GlyphFor(chip.Reference.Kind),
        chip.Reference,
        chip.IsNavigable);

    /// <summary>
    /// Adapts a legacy <see cref="ItemSourceChipVm"/> into the unified Link VM.
    /// <see cref="ItemSourceChipVm.Detail"/> becomes the <see cref="ProvenanceSuffix"/>
    /// (the ItemSourceChip "— from X" bit); the glyph is derived from the optional
    /// <see cref="ItemSourceChipVm.EntityReference"/> kind (<see cref="LinkGlyph.None"/>
    /// when the source maps to no entity).
    /// </summary>
    public static LinkVm From(ItemSourceChipVm chip) => new(
        chip.DisplayName,
        chip.EntityReference is { } r ? GlyphFor(r.Kind) : LinkGlyph.None,
        chip.EntityReference,
        chip.IsNavigable,
        ProvenanceSuffix: string.IsNullOrEmpty(chip.Detail) ? null : chip.Detail);
}
