using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MahApps.Metro.IconPacks;

namespace Mithril.Shared.Wpf;

/// <summary>
/// What a click on a <see cref="Link"/> should do, decided purely from the bound
/// <see cref="LinkVm"/>. Factored out of the event handler so the navigable-vs-pending
/// branch is unit-testable without spinning up the visual tree.
/// </summary>
public enum LinkClickAction
{
    /// <summary>VM is null / has no reference and isn't navigable — do nothing.</summary>
    None,

    /// <summary>Navigable + has a reference: invoke the host's command with the reference.</summary>
    Navigate,

    /// <summary>
    /// G-c pending-availability: target tab not shipped. Still a Link — copy the
    /// canonical <see cref="LinkVm.DisplayName"/> to the clipboard with a transient ack.
    /// </summary>
    CopyName,
}

/// <summary>
/// What the lead element of a <see cref="Link"/> should render, decided purely from
/// the bound <see cref="LinkVm"/>. The G3-amended hybrid icon family: a CDN sprite
/// when present (preferred), else the type-coded Lucide fallback, else nothing.
/// Factored out (mirroring <see cref="LinkClickAction"/> / <see cref="Link.ResolveClick"/>)
/// so the sprite-vs-Lucide-vs-none branch is unit-testable without the visual tree.
/// </summary>
public enum LinkLeadKind
{
    /// <summary>No <see cref="LinkVm.IconId"/> and <see cref="LinkGlyph.None"/> — no lead element.</summary>
    None,

    /// <summary>Real CDN game-art sprite (<see cref="LinkVm.IconId"/> &gt; 0). Wins over Lucide.</summary>
    Sprite,

    /// <summary>No sprite; type-coded Lucide fallback (<see cref="LinkVm.Glyph"/> != None).</summary>
    Lucide,
}

/// <summary>
/// Discriminated result of <see cref="Link.ResolveLead(LinkVm)"/>. Exactly one of
/// <see cref="IconId"/> (when <see cref="Kind"/> is <see cref="LinkLeadKind.Sprite"/>)
/// or <see cref="LucideKind"/> (when <see cref="LinkLeadKind.Lucide"/>) is meaningful;
/// <see cref="LinkLeadKind.None"/> carries neither.
/// </summary>
public readonly record struct LinkLead(
    LinkLeadKind Kind,
    int IconId,
    PackIconLucideKind LucideKind)
{
    /// <summary>Sprite lead: render the CDN art for <paramref name="iconId"/>.</summary>
    public static LinkLead Sprite(int iconId) =>
        new(LinkLeadKind.Sprite, iconId, PackIconLucideKind.None);

    /// <summary>Lucide fallback lead: render <paramref name="lucide"/>.</summary>
    public static LinkLead Lucide(PackIconLucideKind lucide) =>
        new(LinkLeadKind.Lucide, 0, lucide);

    /// <summary>No lead element.</summary>
    public static readonly LinkLead None = new(LinkLeadKind.None, 0, PackIconLucideKind.None);
}

/// <summary>
/// The Phase-4 shared <b>Link</b> primitive (G3 visual grammar · "Link · navigates ·
/// V2"). One templated control subsuming both <see cref="EntityChip"/> and
/// <see cref="ItemSourceChip"/>; DataContext is a <see cref="LinkVm"/>.
/// <para>
/// Visual target (strictly per <c>docs/silmarillion-visual-grammar.md</c>): no border,
/// no surface at rest; a 12px <em>lead</em> Lucide glyph before the name; the name in
/// <c>AccentBrush</c> gold at body weight; optional italic quaternary provenance suffix
/// / kind label trailing. Navigable hover paints a 10% gold tint over
/// <c>GrammarRadiusMd</c> with <c>Cursor=Hand</c>.
/// </para>
/// <para>
/// <b>G-c availability degrade (the inverse of EntityChip's legacy behaviour):</b> when
/// <see cref="LinkVm.IsNavigable"/> is false the rest state is <em>visually identical</em>
/// to a navigable Link — same gold, same glyph, zero shipping-schedule leak. Only on
/// interaction does it differ: hover shows a neutral (non-gold) tint plus a trailing
/// 11px <c>Copy</c> Lucide glyph, and click copies <see cref="LinkVm.DisplayName"/> to
/// the clipboard with a brief transient ack (mirroring <see cref="DetailExportHost"/>).
/// </para>
/// Default style ships in <c>Mithril.Shared.Wpf/Resources.xaml</c> (mirrors the
/// <see cref="DetailExportHost"/> / <see cref="ViewModeToggle"/> templated-control
/// pattern).
/// </summary>
public sealed class Link : Control
{
    static Link()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(Link), new FrameworkPropertyMetadata(typeof(Link)));
    }

    /// <summary>
    /// Parent-supplied navigation command. Bound on the control itself, not on the
    /// <see cref="LinkVm"/> — the VM is a data carrier; the command is supplied by the
    /// hosting view. Receives the VM's <see cref="LinkVm.Reference"/> as its parameter.
    /// Mirrors <see cref="EntityChip.ClickCommand"/>.
    /// </summary>
    public ICommand? ClickCommand
    {
        get => (ICommand?)GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }

    public static readonly DependencyProperty ClickCommandProperty = DependencyProperty.Register(
        nameof(ClickCommand),
        typeof(ICommand),
        typeof(Link),
        new PropertyMetadata(null));

    /// <summary>
    /// Row-density — the G3-amend-2 sole sizing input (<c>docs/silmarillion-visual-grammar.md</c>
    /// · em size table). Drives <em>only</em> the lead element's em factor (see
    /// <see cref="LeadFactor"/>); nothing else about the Link changes. Defaults to
    /// <see cref="LinkDensity.Prose"/> so existing wrapped-chip call sites keep their
    /// inline layout. The Style's lead-size triggers key off this DP.
    /// </summary>
    public LinkDensity Density
    {
        get => (LinkDensity)GetValue(DensityProperty);
        set => SetValue(DensityProperty, value);
    }

    public static readonly DependencyProperty DensityProperty = DependencyProperty.Register(
        nameof(Density),
        typeof(LinkDensity),
        typeof(Link),
        new PropertyMetadata(LinkDensity.Prose));

    private static readonly DependencyPropertyKey CopiedKey =
        DependencyProperty.RegisterReadOnly(
            nameof(Copied), typeof(bool), typeof(Link),
            new PropertyMetadata(false));

    public static readonly DependencyProperty CopiedProperty = CopiedKey.DependencyProperty;

    /// <summary>True for ~1.2s after a pending (non-navigable) click copies the name;
    /// the template flips to a "copied" acknowledgement while set.</summary>
    public bool Copied => (bool)GetValue(CopiedProperty);

    private static readonly TimeSpan AckHold = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Maps a Link's <see cref="LinkGlyph"/> type-code to a concrete Lucide glyph kind.
    /// <see cref="LinkGlyph.None"/> has no glyph and is not mapped (callers hide the
    /// glyph element). Verified exact against MahApps.Metro.IconPacks.Lucide 6.0.0.
    /// </summary>
    public static PackIconLucideKind ToLucideKind(LinkGlyph glyph) => glyph switch
    {
        LinkGlyph.Skill => PackIconLucideKind.Sparkles,
        LinkGlyph.Recipe => PackIconLucideKind.FlaskConical,
        LinkGlyph.Ingredient => PackIconLucideKind.FlaskRound,
        LinkGlyph.Npc => PackIconLucideKind.UserRound,
        LinkGlyph.Location => PackIconLucideKind.MapPin,
        LinkGlyph.Item => PackIconLucideKind.Package,
        LinkGlyph.CombatAbility => PackIconLucideKind.Sword,
        // LinkGlyph.None — no lead glyph; element is hidden, kind is unused.
        _ => PackIconLucideKind.None,
    };

    /// <summary>
    /// Pure decision: given a (possibly null) bound VM, what should a click do? Factored
    /// out of <see cref="OnClick"/> so the navigable-vs-pending branch is unit-testable
    /// without the visual tree. Navigable + reference → <see cref="LinkClickAction.Navigate"/>;
    /// any other non-null VM → <see cref="LinkClickAction.CopyName"/> (G-c: never a dead
    /// click); null VM → <see cref="LinkClickAction.None"/>.
    /// </summary>
    public static LinkClickAction ResolveClick(LinkVm? vm)
    {
        if (vm is null) return LinkClickAction.None;
        if (vm.IsNavigable && vm.Reference is not null) return LinkClickAction.Navigate;
        return LinkClickAction.CopyName;
    }

    /// <summary>
    /// Pure decision (G3 amendment 2026-05-17): given a (possibly null) bound VM, what
    /// should the 12px lead element render? The hybrid icon family — a real CDN sprite
    /// is <em>preferred</em> when present (<see cref="LinkVm.IconId"/> &gt; 0 ⇒
    /// <see cref="LinkLead.Sprite"/>, beating any <see cref="LinkVm.Glyph"/>); else the
    /// type-coded Lucide fallback (<see cref="LinkVm.Glyph"/> != <see cref="LinkGlyph.None"/>
    /// ⇒ <see cref="LinkLead.Lucide"/>); else <see cref="LinkLead.None"/>. Mirrors
    /// <see cref="ResolveClick"/> so it's unit-testable without the visual tree. Does
    /// not touch the G-c degrade / pending behaviour — the amendment is lead-only.
    /// </summary>
    public static LinkLead ResolveLead(LinkVm? vm)
    {
        if (vm is null) return LinkLead.None;
        if (vm.IconId > 0) return LinkLead.Sprite(vm.IconId);
        if (vm.Glyph != LinkGlyph.None) return LinkLead.Lucide(ToLucideKind(vm.Glyph));
        return LinkLead.None;
    }

    /// <summary>
    /// Pure G3-amend-2 lead em-factor: the multiplier applied to the inherited
    /// <c>FontSize</c> (via <see cref="FontSizeTimesConverter"/>) to size the lead
    /// element, decided solely by <paramref name="density"/> × the lead family
    /// (<paramref name="lead"/>). The exact ratified table
    /// (<c>docs/silmarillion-visual-grammar.md</c> · "All sizes are em-relative"):
    /// <list type="bullet">
    ///   <item>Sprite — Prose <c>1.0</c>, List <c>1.5</c></item>
    ///   <item>Lucide — Prose <c>0.75</c>, List <c>1.125</c></item>
    /// </list>
    /// <see cref="LinkLeadKind.None"/> has no lead element, so its factor is
    /// irrelevant (returns <c>0</c>). Factored out (mirroring <see cref="ResolveLead"/> /
    /// <see cref="ResolveClick"/>) so the density→size math is unit-testable without
    /// the visual tree. <b>This supersedes the prior fixed-12px lead</b> (commit
    /// <c>ec0a49e</c>): the lead is now em-relative, not a hard 12px.
    /// </summary>
    public static double LeadFactor(LinkDensity density, LinkLeadKind lead) => lead switch
    {
        LinkLeadKind.Sprite => density == LinkDensity.List ? 1.5 : 1.0,
        LinkLeadKind.Lucide => density == LinkDensity.List ? 1.125 : 0.75,
        _ => 0.0,
    };

    private Button? _button;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_button is not null)
            _button.Click -= OnClick;

        _button = GetTemplateChild("PART_Button") as Button;

        if (_button is not null)
            _button.Click += OnClick;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as LinkVm;
        switch (ResolveClick(vm))
        {
            case LinkClickAction.Navigate:
                var cmd = ClickCommand;
                if (cmd is not null && cmd.CanExecute(vm!.Reference))
                    cmd.Execute(vm.Reference);
                break;

            case LinkClickAction.CopyName:
                CopyNameToClipboard(vm!.DisplayName);
                break;

            case LinkClickAction.None:
            default:
                break;
        }
    }

    // Mirrors DetailExportHost.OnFooterClick: try/catch the clipboard (it can transiently
    // fail), then a one-shot DispatcherTimer drives the ~1.2s transient ack.
    private void CopyNameToClipboard(string name)
    {
        if (string.IsNullOrEmpty(name))
            return;

        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, name), copy: true);
        }
        catch
        {
            return; // clipboard can transiently fail; no ack, user can retry
        }

        SetValue(CopiedKey, true);
        var timer = new DispatcherTimer { Interval = AckHold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SetValue(CopiedKey, false);
        };
        timer.Start();
    }
}
