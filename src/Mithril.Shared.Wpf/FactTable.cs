using System.Windows;
using System.Windows.Controls;

namespace Mithril.Shared.Wpf;

/// <summary>
/// The Phase-4 shared <b>FactTable</b> primitive (G3 visual grammar · "Fact · inert ·
/// weight-axis" + Phase-4 carry-forward #3). ONE polymorphic templated content control
/// that renders a Fact label/value group in <em>three</em> layouts —
/// <see cref="FactTableLayout.Strip"/> (horizontal dot-separated stat strip) ↔
/// <see cref="FactTableLayout.Grid"/> (vertical 2-column capacity grid) ↔
/// <see cref="FactTableLayout.Scalar"/> (a single bare value) — from one
/// <see cref="FactTableVm"/>. DataContext is a <see cref="FactTableVm"/>.
/// <para>
/// <b>Anti-fork (carry-forward #3).</b> The recipe-header stat strip and the
/// StorageVault favor-tier capacity table are the same data rotated, and a flat
/// <c>16 slots</c> chest is the degenerate case — all THREE are this ONE control with
/// a <see cref="LayoutProperty"/> the default Style <c>DataTrigger</c>s on. There are
/// deliberately no per-shape controls (the identical anti-fork rationale as the
/// single-<see cref="Link"/> mandate subsuming <see cref="EntityChip"/>/
/// <see cref="ItemSourceChip"/>).
/// </para>
/// <para>
/// <b>Inert per G-b.</b> Fact is the only fully inert tier: no border, no surface, NO
/// gold on values (value text is <c>TextPrimaryBrush</c>, labels
/// <c>TextTertiaryBrush</c>), zero hover, zero interactivity. There is intentionally
/// <em>no</em> <c>ClickCommand</c> / <c>OnApplyTemplate</c> wiring — unlike
/// <see cref="Link"/>/<see cref="SetRef"/> this control has nothing to click. The
/// optional footer-quiet <see cref="FactTableVm.Quiet"/> weight (mono +
/// <c>TextTertiaryBrush</c>) is the only weight-axis concession (orthogonal/P2),
/// default off.
/// </para>
/// Default style ships in <c>Mithril.Shared.Wpf/Resources.xaml</c> (appended after the
/// <see cref="SetRef"/> style, same <see cref="DefaultStyleKeyProperty"/>
/// <c>OverrideMetadata</c> pattern). The Style mirrors the bound VM's
/// <see cref="FactTableVm.Layout"/> onto <see cref="LayoutProperty"/> and switches the
/// presented template with <c>DataTrigger</c>s on it — one control, no fork.
/// </summary>
public sealed class FactTable : Control
{
    static FactTable()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(FactTable), new FrameworkPropertyMetadata(typeof(FactTable)));
    }

    /// <summary>
    /// The active layout. The default Style binds this to the bound
    /// <see cref="FactTableVm.Layout"/> and <c>DataTrigger</c>s the presented
    /// template off it, so the polymorphic strip/grid/scalar switch is a single
    /// layout-driven control rather than three forked controls (carry-forward #3).
    /// Exposed as a DP (not read straight off the VM in XAML) purely so the
    /// <c>DataTrigger</c>s have a control-level enum target — the VM stays the
    /// single source of truth via the Style's binding.
    /// </summary>
    public FactTableLayout Layout
    {
        get => (FactTableLayout)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout),
        typeof(FactTableLayout),
        typeof(FactTable),
        new PropertyMetadata(FactTableLayout.Strip));
}
