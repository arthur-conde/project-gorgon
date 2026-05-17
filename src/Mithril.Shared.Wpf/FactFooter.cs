using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Mithril.Shared.Wpf;

/// <summary>
/// What a click on a single <see cref="FactFooterId"/> cell should do, decided purely
/// from the cell. Factored out of the click handler so the G-a copyable-iff-KEY branch
/// is unit-testable without spinning up the visual tree (mirrors
/// <see cref="Link.ResolveClick"/> / <see cref="SetRef.ResolveClick"/>).
/// </summary>
public enum FactFooterCellAction
{
    /// <summary>Cell is null — do nothing.</summary>
    None,

    /// <summary>
    /// G-a inert cell (<see cref="FactFooterId.Copyable"/> false — a storage-only
    /// <c>ROW</c> key). No glyph, no hover, click does nothing. A deliberate no-op:
    /// the grammar specifies <em>no</em> behaviour for an inert footer ID.
    /// </summary>
    Inert,

    /// <summary>
    /// G-a copyable cell (<see cref="FactFooterId.Copyable"/> true — a cross-entity
    /// reference <c>KEY</c>). Copy exactly this cell's
    /// <see cref="FactFooterId.Value"/> (no label, no separator) with a transient
    /// per-cell ack.
    /// </summary>
    Copy,
}

/// <summary>
/// The Phase-4 shared <b>FactFooter</b> primitive (G3 visual grammar · "G-a — Fact
/// identifiers, copyable without becoming Control"). One templated control encoding the
/// footer-ID strip as a standalone primitive; DataContext is a
/// <see cref="FactFooterVm"/>.
/// <para>
/// <b>It is a Fact, not a Control — location, not chassis.</b> The strip lives below a
/// thin <c>BorderFaintBrush</c> top divider, beneath the read-flow, with no surface / no
/// fill / no border of its own. A Control declares itself at rest (a chassis); this
/// declares itself only on contact — the 11px <c>Copy</c> Lucide glyph is
/// hover-revealed, never shown at rest. Scan-time read is "another fact under the
/// divider"; the copy affordance is <em>discovered</em>. This is exactly the G-a
/// "why this isn't Control" reasoning.
/// </para>
/// <para>
/// <b>Copyable iff cross-entity reference key (the G-a discriminator).</b> A
/// <c>KEY</c> cell (<see cref="FactFooterId.Copyable"/> true) hover-reveals the copy
/// glyph, takes <c>Cursor=Hand</c>, and click copies <em>exactly</em> that cell's
/// <see cref="FactFooterId.Value"/> with a transient ~1.2s per-cell ack. A <c>ROW</c>
/// cell (<see cref="FactFooterId.Copyable"/> false) is inert: <c>Cursor=Default</c>, no
/// hover state, no glyph ever, click does nothing. The discriminator is the explicit
/// <see cref="FactFooterId.Copyable"/> bool, never inferred from the tag string.
/// </para>
/// <para>
/// <b>Per-cell ack, mirroring <see cref="DetailExportHost"/> without touching it.</b>
/// This control owns the clipboard write + one-shot <c>DispatcherTimer</c> exactly as
/// <c>DetailExportHost.CopySegment</c> does (try/catch the clipboard — it can
/// transiently fail; on success set the cell's <see cref="FactFooterId.Copied"/> and
/// clear it after <c>AckHold</c>). The ack is on the bound <see cref="FactFooterId"/>
/// (an <see cref="FooterSegmentItem"/>-shaped <c>ObservableObject</c>), so only the
/// clicked cell flips — the other cell is unaffected, precisely like
/// <c>FooterSegmentItem.Copied</c>. <c>DetailExportHost</c> is the read-only reference;
/// it is not modified — this is a standalone coexisting primitive (Phase 5 migrates
/// hosts).
/// </para>
/// Default style ships in <c>Mithril.Shared.Wpf/Resources.xaml</c> (appended after the
/// <see cref="FactTable"/> style, same <see cref="DefaultStyleKeyProperty"/>
/// <c>OverrideMetadata</c> pattern as the other Phase-4 primitives).
/// </summary>
public sealed class FactFooter : Control
{
    static FactFooter()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(FactFooter), new FrameworkPropertyMetadata(typeof(FactFooter)));
    }

    private static readonly TimeSpan AckHold = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Pure decision: given a (possibly null) footer-ID cell, what should a click do?
    /// Factored out of <see cref="OnCellClick"/> so the G-a copyable-iff-KEY branch is
    /// unit-testable without the visual tree (mirrors <see cref="Link.ResolveClick"/>).
    /// Copyable cell → <see cref="FactFooterCellAction.Copy"/>; any other non-null cell
    /// → <see cref="FactFooterCellAction.Inert"/> (the storage-only no-op); null cell →
    /// <see cref="FactFooterCellAction.None"/>. The decision is driven solely by
    /// <see cref="FactFooterId.Copyable"/> — never by <see cref="FactFooterId.LabelTag"/>.
    /// </summary>
    public static FactFooterCellAction ResolveCellClick(FactFooterId? cell)
    {
        if (cell is null) return FactFooterCellAction.None;
        return cell.Copyable ? FactFooterCellAction.Copy : FactFooterCellAction.Inert;
    }

    private readonly List<ButtonBase> _cellButtons = [];

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        foreach (var b in _cellButtons)
            b.Click -= OnCellClick;
        _cellButtons.Clear();

        // The template realises one ButtonBase per cell from an ItemsControl over
        // FactFooterVm.Ids. Walk the realised children once the template is applied
        // and wire each cell's click (mirrors the PART_-button wiring of the other
        // primitives — here it is per-item rather than a single named PART).
        if (GetTemplateChild("PART_Cells") is ItemsControl cells)
        {
            cells.Loaded += (_, _) => WireCellButtons(cells);
            WireCellButtons(cells);
        }
    }

    private void WireCellButtons(ItemsControl cells)
    {
        foreach (var b in _cellButtons)
            b.Click -= OnCellClick;
        _cellButtons.Clear();

        foreach (var btn in EnumerateButtons(cells))
        {
            btn.Click += OnCellClick;
            _cellButtons.Add(btn);
        }
    }

    private static IEnumerable<ButtonBase> EnumerateButtons(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ButtonBase b)
                yield return b;
            else
                foreach (var nested in EnumerateButtons(child))
                    yield return nested;
        }
    }

    private void OnCellClick(object sender, RoutedEventArgs e)
    {
        var cell = (sender as FrameworkElement)?.DataContext as FactFooterId;
        switch (ResolveCellClick(cell))
        {
            case FactFooterCellAction.Copy:
                CopyCell(cell!);
                break;

            // Inert (storage-only ROW): the grammar specifies no behaviour for an
            // inert footer ID — do the minimal safe thing (nothing). No hover/glyph
            // is shown for it either (Style), so this branch is defensive.
            case FactFooterCellAction.Inert:
            case FactFooterCellAction.None:
            default:
                break;
        }
    }

    // Mirrors DetailExportHost.CopySegment verbatim in shape: try/catch the clipboard
    // (it can transiently fail — no ack, user retries), then a one-shot DispatcherTimer
    // drives the ~1.2s transient ack on JUST this cell's Copied (the other cell is
    // untouched, exactly like FooterSegmentItem.Copied). DetailExportHost is the
    // read-only reference and is not modified.
    private static void CopyCell(FactFooterId cell)
    {
        if (string.IsNullOrEmpty(cell.Value))
            return;

        try
        {
            Clipboard.SetDataObject(
                new DataObject(DataFormats.UnicodeText, cell.Value), copy: true);
        }
        catch
        {
            return; // clipboard can transiently fail; no ack, user can retry
        }

        cell.Copied = true;
        var timer = new DispatcherTimer { Interval = AckHold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            cell.Copied = false;
        };
        timer.Start();
    }
}
