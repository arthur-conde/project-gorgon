using System.Collections.Generic;
using Mithril.Shared.Wpf;

namespace Silmarillion.ViewModels;

/// <summary>
/// PlayerTitle detail-pane view-model. Sections top-down:
/// <list type="number">
/// <item><b>Header</b> — the clean title (large; <c>&lt;color&gt;</c> markup
/// stripped per #248 Option A), with the <c>Title_N</c> envelope key as the
/// mono bottom-right footer (the detail-view internal-name footer convention;
/// the POCO carries no InternalName, so the envelope key <i>is</i> the
/// identifier and the footer is the single key, not a divergent pair).</item>
/// <item><b>How to earn</b> — <see cref="Mithril.Reference.Models.Misc.PlayerTitle.Tooltip"/>
/// through the shared <c>FormattedText</c> renderer; an italic placeholder when
/// null (many titles have no tooltip).</item>
/// <item><b>Scope / obtainability</b> — Account-wide / Soul-wide badges shown
/// <i>only when true</i> (the false/null default is noise — cookbook
/// *Default-value noise filtering*), plus a "not currently obtainable" note for
/// the <c>Lint_*</c> dev/non-earnable family.</item>
/// </list>
/// <para>
/// <b>#248 — no "Quests awarding this title" surface.</b> Quests do grant titles
/// (<c>Rewards_Effects "BestowTitle(&lt;arg&gt;)"</c>), but the BestowTitle
/// argument is a free-form slug in a different namespace with no structured key
/// relationship to the <c>Title_N</c> envelope keys (the POCO carries no matching
/// identifier). Per #248 the linkage is NOT synthesised — there is no
/// popup-from-index, no affordance, no synthetic <c>EntityKind</c>, no deep-link.
/// See the PR body for the data finding.
/// </para>
/// </summary>
public sealed class PlayerTitleDetailViewModel
{
    public PlayerTitleDetailViewModel(PlayerTitleListRow row)
    {
        Row = row;
        DisplayName = row.DisplayTitle;
        EnvelopeKey = row.EnvelopeKey;
        Tooltip = string.IsNullOrEmpty(row.Title.Tooltip) ? null : row.Title.Tooltip;
        AccountWide = row.AccountWide;
        SoulWide = row.SoulWide;
        IsObtainable = row.IsObtainable;

        // ── Phase 5 grammar-primitive projections ──────────────────────────────
        // The legacy bool/string members above stay (the existing tests + the
        // detail-pane contract); these are the grammar-tier carriers the view
        // binds. Built here (not in the tab VM) because the VM already holds
        // every source datum — the Phase-5 mapping is mechanical, not
        // data-bearing. PlayerTitle is the #404-classified pure Fact+Structure+
        // Control view (no Link/Set-reference at all), so the only carriers are
        // an inert Fact strip and the Fact footer.

        // Scope / obtainability strip (matrix T15 → inert Fact). The three
        // legacy COLOURED semantic badge boxes (Account-wide green / Soul-wide
        // indigo / not-obtainable red) collapse into ONE inert FactTable strip
        // — value-only phrase segments, no box, no pigment (G-b: Fact is inert;
        // the Strip Style carries the only pigment). This is the exact pilot
        // stat-badge-box → strip collapse (RecipeDetailViewModel.StatStrip): a
        // self-describing phrase keeps a null label, empties are skipped, and an
        // all-false set yields StripText "" so the FactTable Style self-hides —
        // the same self-elision the per-badge BoolToVis gave, with the accepted
        // grammar trade-off that the semantic colours drop (consistency over
        // fidelity is the ratified G4 acceptance bar).
        var scope = new List<FactPair>(3);
        if (AccountWide) scope.Add(new FactPair(null, "Account-wide"));
        if (SoulWide) scope.Add(new FactPair(null, "Soul-wide"));
        if (IsNotObtainable) scope.Add(new FactPair(null, "Not currently obtainable"));
        ScopeStrip = FactTableVm.Strip(scope);

        // Footer id (matrix #14 / G-a · ratified E5). PlayerTitle's POCO carries
        // no InternalName — the "Title_NNNN" envelope key IS the only
        // identifier. E5 rule 2: a footer is copyable IFF it is a cross-entity
        // reference key (something else points at it). #248 established the
        // Title_N envelope key has NO structured cross-reference (BestowTitle
        // args live in a different namespace, nothing resolves through it), so
        // it is a display/storage-only key ⇒ the INERT `ROW` cell, never the
        // copyable `KEY`. This is deliberately NOT the pilot's
        // FactFooterVm.Key(InternalName) copyable path — it is the
        // EnvelopeKey-inert path the Recipe pilot never exercised, the first
        // fan-out case that proves the E5 copyable-iff-cross-ref discriminator.
        // None() when somehow keyless so the strip self-hides (G-a: hidden at 0).
        Footer = string.IsNullOrEmpty(EnvelopeKey)
            ? FactFooterVm.None()
            : FactFooterVm.Of(new FactFooterId("ROW", EnvelopeKey, copyable: false));
    }

    public PlayerTitleListRow Row { get; }

    /// <summary>Clean title label — <c>&lt;color&gt;</c> markup stripped (#248 Option A).</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Envelope key (e.g. <c>"Title_5018"</c>). The POCO carries no InternalName,
    /// so the footer is just this single identifier.
    /// </summary>
    public string EnvelopeKey { get; }

    /// <summary>
    /// Legacy footer text — the bare envelope key. Retained (the existing test +
    /// the detail-pane contract); the view now binds <see cref="Footer"/> instead.
    /// </summary>
    public string FooterText => EnvelopeKey;

    /// <summary>
    /// Inert scope/obtainability Fact strip (matrix T15 → Fact-inert). The three
    /// legacy coloured semantic badge boxes collapse into one dot-separated
    /// value-only <see cref="FactTableVm"/> strip (no box, no pigment — G-b).
    /// Empty (all-false) ⇒ <see cref="FactTableVm.StripText"/> is "" so the
    /// shared <c>FactTable</c> Style self-hides, exactly the per-badge
    /// <c>BoolToVis</c> self-elision it replaces. Mirrors the pilot's
    /// <c>RecipeDetailViewModel.StatStrip</c>.
    /// </summary>
    public FactTableVm ScopeStrip { get; }

    /// <summary>
    /// Footer identifier strip (matrix #14, G-a · ratified E5). The
    /// <c>Title_NNNN</c> envelope key is a display/storage-only key (#248: no
    /// cross-entity reference resolves through it) ⇒ a single <b>inert</b>
    /// <c>ROW</c> cell (<see cref="FactFooterId.Copyable"/> false), <em>not</em>
    /// the pilot's copyable <c>KEY</c>. <see cref="FactFooterVm.None"/> (the
    /// strip self-hides) if somehow keyless.
    /// </summary>
    public FactFooterVm Footer { get; }

    /// <summary>"How to earn" prose, or null (italic placeholder shown instead).</summary>
    public string? Tooltip { get; }
    public bool HasTooltip => Tooltip is not null;

    public bool AccountWide { get; }
    public bool SoulWide { get; }

    /// <summary>True unless the title carries a <c>Lint_*</c> dev/non-earnable keyword.</summary>
    public bool IsObtainable { get; }

    /// <summary>Inverse of <see cref="IsObtainable"/> — drives the "not currently obtainable" note.</summary>
    public bool IsNotObtainable => !IsObtainable;
}
