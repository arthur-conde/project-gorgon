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
    }

    public PlayerTitleListRow Row { get; }

    /// <summary>Clean title label — <c>&lt;color&gt;</c> markup stripped (#248 Option A).</summary>
    public string DisplayName { get; }

    /// <summary>
    /// Envelope key (e.g. <c>"Title_5018"</c>). The POCO carries no InternalName,
    /// so the footer is just this single identifier.
    /// </summary>
    public string EnvelopeKey { get; }

    /// <summary>Footer text — the bare envelope key (no divergent pair to render).</summary>
    public string FooterText => EnvelopeKey;

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
