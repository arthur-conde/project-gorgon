using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Mithril.Shared.Wpf;

/// <summary>
/// One footer identifier cell (G-a). A tiny uppercase <see cref="LabelTag"/>
/// (<c>KEY</c> / <c>ROW</c>), the mono <see cref="Value"/>, and the G-a discriminator
/// <see cref="Copyable"/>.
/// <para>
/// <b>The discriminator is an explicit bool, never inferred from the tag string.</b>
/// G-a: a footer ID is copyable <em>iff</em> it is a cross-entity reference key
/// (InternalName / title key — the <c>KEY</c> tag). A storage-only key
/// (EnvelopeKey-style — the <c>ROW</c> tag) is inert. The ratified E5 rule decides
/// copyability from the <em>data</em> (Phase 5 wires it), so this is a flag the call
/// site sets, not something <see cref="FactFooter"/> derives by matching
/// <see cref="LabelTag"/> == "KEY". Keeping them orthogonal means a future tag that is
/// also a reference key (or a <c>KEY</c> that happens to be storage-only) is expressible
/// without lying about the affordance.
/// </para>
/// <see cref="ObservableObject"/> with a transient <see cref="Copied"/> ack — mirrors
/// <see cref="FooterSegmentItem"/>: the ack is <em>per cell</em>, the other cell is
/// unaffected. <see cref="Copied"/> is driven by <see cref="FactFooter"/>'s one-shot
/// <c>DispatcherTimer</c> exactly as <c>DetailExportHost.CopySegment</c> drives
/// <see cref="FooterSegmentItem.Copied"/>.
/// </summary>
public sealed partial class FactFooterId : ObservableObject
{
    /// <param name="labelTag">
    /// Tiny uppercase tag (<c>KEY</c> = cross-entity reference identifier;
    /// <c>ROW</c> = storage-only key). Display only — does NOT decide copyability.
    /// </param>
    /// <param name="value">The atomic identifier shown (mono) and, if copyable,
    /// copied verbatim.</param>
    /// <param name="copyable">
    /// The G-a discriminator: <see langword="true"/> ONLY for a cross-entity
    /// reference key (<c>KEY</c>); <see langword="false"/> for a storage-only key
    /// (<c>ROW</c>). Set explicitly by the call site from the data per the ratified
    /// E5 rule — never inferred from <paramref name="labelTag"/>.
    /// </param>
    public FactFooterId(string labelTag, string value, bool copyable)
    {
        LabelTag = labelTag;
        Value = value;
        Copyable = copyable;
    }

    /// <summary>Tiny uppercase tag, e.g. <c>KEY</c> / <c>ROW</c>. Display only.</summary>
    public string LabelTag { get; }

    /// <summary>The atomic identifier; shown mono and (iff <see cref="Copyable"/>)
    /// the exact, whole copy payload — no label, no separator.</summary>
    public string Value { get; }

    /// <summary>G-a: copyable iff a cross-entity reference key (KEY). Inert otherwise
    /// (ROW). Explicit — not inferred from <see cref="LabelTag"/>.</summary>
    public bool Copyable { get; }

    /// <summary>True for ~1.2s after THIS cell is copied; the Style reveals a
    /// "copied" ack on just this cell (the other cell is untouched — exactly like
    /// <see cref="FooterSegmentItem.Copied"/>).</summary>
    [ObservableProperty]
    private bool _copied;
}

/// <summary>
/// The Phase-4 shared <b>FactFooter</b> data-carrier (G3 visual grammar · "G-a — Fact
/// identifiers, copyable without becoming Control"). An ordered list of 0, 1, or 2
/// <see cref="FactFooterId"/>; DataContext for the <see cref="FactFooter"/> control.
/// <para>
/// <b>Location, not chassis (why this is a Fact, not a Control).</b> The strip lives
/// <em>below</em> a thin top-divider, beneath the read-flow — a Control declares itself
/// at rest (chassis); this declares itself only on contact (the copy glyph is
/// hover-discovered, never shown at rest). Scan-time read is "another fact under the
/// divider".
/// </para>
/// <para>
/// <b>The grammar caps at 2 identifiers.</b> <see cref="Of"/> throws on &gt;2 — 0/1/2
/// is a ratified G-a invariant, not a soft guideline, so an over-count is a
/// programming error surfaced loudly rather than silently truncated.
/// </para>
/// </summary>
public sealed class FactFooterVm
{
    /// <summary>The inert middot the Style places <em>between</em> the two cells. It
    /// is NEVER part of any cell's copy payload (mirrors
    /// <see cref="FactTableVm.StripSeparator"/> / <c>FooterSegmentItem</c>'s
    /// "separator never in the copy payload" guarantee).</summary>
    public const string CellSeparator = " · ";

    /// <summary>The grammar caps footer identifiers at 0/1/2 (G-a).</summary>
    public const int MaxIds = 2;

    private FactFooterVm(IReadOnlyList<FactFooterId> ids) => Ids = ids;

    /// <summary>The 0, 1, or 2 footer identifiers, in render order (preserved
    /// verbatim — the Style never reorders).</summary>
    public IReadOnlyList<FactFooterId> Ids { get; }

    /// <summary>True only when there is ≥1 id; at 0 the control renders nothing
    /// (Collapsed) — there is no divider with nothing under it.</summary>
    public bool HasIds => Ids.Count > 0;

    /// <summary>0 identifiers — the strip is hidden entirely (G-a: "Strip hidden at 0").</summary>
    public static FactFooterVm None() => new([]);

    /// <summary>The common single cross-entity reference key: one copyable
    /// <c>KEY</c> (InternalName / title key).</summary>
    public static FactFooterVm Key(string value) =>
        new([new FactFooterId("KEY", value, copyable: true)]);

    /// <summary>
    /// General constructor. Order is preserved verbatim. Throws if more than
    /// <see cref="MaxIds"/> are supplied — the 0/1/2 cap is a ratified G-a invariant,
    /// so an over-count is a loud programming error, never a silent truncation.
    /// </summary>
    public static FactFooterVm Of(params FactFooterId[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Length > MaxIds)
            throw new ArgumentException(
                $"The G-a footer grammar caps identifiers at {MaxIds} (got {ids.Length}). " +
                "0/1/2 is a ratified invariant — do not exceed it.",
                nameof(ids));

        return new(ids.ToList());
    }
}
