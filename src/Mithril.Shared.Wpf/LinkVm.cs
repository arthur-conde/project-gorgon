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

    /// <summary>
    /// Treasure-System pool / profile (abstract membership). The ratified #435 spec
    /// assigns the <c>layers</c> Lucide to the Power-detail "Appears in pools" Links.
    /// </summary>
    Pool,

    /// <summary>
    /// Treasure-System power (abstract). The ratified #435 spec assigns the <c>zap</c>
    /// Lucide to the per-power Links in the Profile/Pool detail's power list.
    /// </summary>
    Power,
}

/// <summary>
/// Row-density of a <see cref="Link"/> — the G3-amend-2 sole sizing input
/// (<c>docs/silmarillion-visual-grammar.md</c> · "Link · navigates · V2", "Shape ·
/// spacing", and the em size table). It selects the em factor the lead element scales
/// by; <see cref="Prose"/> is the default (inline in a sentence, single Link or short
/// list). <see cref="List"/> is own-line-per-entry — a layout-changing per-section
/// design call, deliberately NOT applied by the Recipe pilot (left a G4 decision).
/// </summary>
public enum LinkDensity
{
    /// <summary>Inline in a sentence / short list. Sprite ×1.0em, Lucide ×0.75em.</summary>
    Prose,

    /// <summary>Own line per entry. Sprite ×1.5em, Lucide ×1.125em (line-height ~1.7).</summary>
    List,
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
/// <param name="Glyph">
/// The 12px lead Lucide <em>fallback</em> glyph type-code; <see cref="LinkGlyph.None"/>
/// for no glyph. Per the G3 amendment (2026-05-17) the lead element is a <b>hybrid</b>:
/// a real CDN sprite (<see cref="IconId"/> &gt; 0) is preferred when present; this
/// type-coded Lucide is the fallback for abstract refs that have no sprite. Always
/// set it — icon-less refs still need it.
/// </param>
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
/// <param name="IconId">
/// Real CDN game-art sprite id (G3 amendment 2026-05-17). <c>&gt; 0</c> ⇒ the lead
/// element renders this sprite via <see cref="IconImage"/> (preferred — tangible
/// nouns: items, recipes, NPCs, monsters). <c>0</c> ⇒ no sprite; the lead falls back
/// to the type-coded Lucide <see cref="Glyph"/> (abstract refs: skills, abilities,
/// locations, keywords, factions). The sprite, when present, wins over
/// <see cref="Glyph"/> — both render at the 12px lead position.
/// </param>
/// <param name="IsUnconfirmed">
/// G-d reference-state axis (2026-05-17, #431). True when this Link is a declared
/// edge the inverse data does not corroborate (a <c>sources_*.json</c> assertion
/// the <c>recipes.json</c>/<c>quests.json</c> reverse does not back). Renders the
/// gold name with a dashed underline + a one-word <c>· unconfirmed</c> tail; the
/// full caveat is the control <see cref="UnconfirmedTooltip"/>. <b>Orthogonal to
/// <see cref="IsNavigable"/></b> (the Degraded/G-c axis) — a Link can be both; the
/// two signals live on different sub-elements and stay legible. Additive flag,
/// deliberately not a single <c>state</c> enum (an enum can't express the compose
/// case). False everywhere it doesn't apply; <see cref="ProvenanceSuffix"/> stays
/// reserved for real provenance and is never double-loaded with the caveat.
/// </param>
/// <param name="UnconfirmedTooltip">
/// The long-form data-integrity caveat shown as the control <c>ToolTip</c> when
/// <see cref="IsUnconfirmed"/>. Null when not unconfirmed (or no caveat text).
/// </param>
public sealed record LinkVm(
    string DisplayName,
    LinkGlyph Glyph,
    EntityRef? Reference,
    bool IsNavigable,
    string? ProvenanceSuffix = null,
    string? KindLabel = null,
    int IconId = 0,
    bool IsUnconfirmed = false,
    string? UnconfirmedTooltip = null)
{
    /// <summary>
    /// True when a real CDN sprite is present (<see cref="IconId"/> &gt; 0) and should
    /// be the lead element. Drives the Style's mutually-exclusive sprite-vs-Lucide
    /// switch object-safely (int&gt;0 → bool DataTrigger; the repo-preferred pattern
    /// over a converter for an int predicate).
    /// </summary>
    public bool HasSprite => IconId > 0;
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
        EntityKind.Profile => LinkGlyph.Pool,
        EntityKind.Power => LinkGlyph.Power,
        _ => LinkGlyph.None,
    };

    /// <summary>
    /// Adapts a legacy <see cref="EntityChipVm"/> into the unified Link VM. Per the G3
    /// amendment the chip's <see cref="EntityChipVm.IconId"/> rides through as the
    /// preferred lead sprite; the kind-derived <see cref="LinkGlyph"/> is kept as the
    /// Lucide fallback for icon-less (abstract) refs.
    /// </summary>
    public static LinkVm From(EntityChipVm chip) => new(
        chip.DisplayName,
        GlyphFor(chip.Reference.Kind),
        chip.Reference,
        chip.IsNavigable,
        IconId: chip.IconId);

    /// <summary>
    /// Adapts a legacy <see cref="ItemSourceChipVm"/> into the unified Link VM.
    /// <see cref="ItemSourceChipVm.Detail"/> becomes the <see cref="ProvenanceSuffix"/>
    /// (the ItemSourceChip "— from X" bit); the kind-derived <see cref="LinkGlyph"/>
    /// (<see cref="LinkGlyph.None"/> when the source maps to no entity) is the Lucide
    /// fallback. Per the G3 amendment <see cref="ItemSourceChipVm.IconId"/> (null ⇒ 0)
    /// rides through as the preferred lead sprite. G-d (#431): the chip's
    /// <see cref="ItemSourceChipVm.IsUnconfirmed"/> / <see cref="ItemSourceChipVm.UnconfirmedTooltip"/>
    /// ride through so a declared-but-uncorroborated source renders the dashed
    /// reference-state treatment instead of an overloaded provenance suffix.
    /// </summary>
    public static LinkVm From(ItemSourceChipVm chip) => new(
        chip.DisplayName,
        chip.EntityReference is { } r ? GlyphFor(r.Kind) : LinkGlyph.None,
        chip.EntityReference,
        chip.IsNavigable,
        ProvenanceSuffix: string.IsNullOrEmpty(chip.Detail) ? null : chip.Detail,
        IconId: chip.IconId ?? 0,
        IsUnconfirmed: chip.IsUnconfirmed,
        UnconfirmedTooltip: chip.UnconfirmedTooltip);
}
