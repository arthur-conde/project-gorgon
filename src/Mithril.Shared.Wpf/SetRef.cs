using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mithril.Shared.Wpf;

/// <summary>
/// What a click on a <see cref="SetRef"/> should do, decided purely from the bound
/// <see cref="SetRefVm"/>. Factored out of the event handler so the
/// actionable-vs-unwired branch is unit-testable without the visual tree (mirrors
/// <see cref="LinkClickAction"/> / <see cref="Link.ResolveClick"/>).
/// </summary>
public enum SetRefClickAction
{
    /// <summary>VM is null — do nothing.</summary>
    None,

    /// <summary>Actionable Set-ref: invoke the host's command with the VM.</summary>
    Activate,

    /// <summary>
    /// Availability corollary: the Set-ref is real and renders on the full blue
    /// chassis, but its reveal/filter action is not yet wired. The grammar specifies
    /// <em>no</em> behaviour for an un-wired Set-ref click, so this is the minimal
    /// safe thing — a deliberate no-op (no command, no exception, no invented UI).
    /// Flagged as an under-spec in the Phase-4 report.
    /// </summary>
    Unavailable,
}

/// <summary>
/// The Phase-4 shared <b>SetRef</b> primitive (G3 visual grammar ·
/// "Set-reference · filter / keyword / group / stacking" + carry-forward #4). One
/// polymorphic templated control carrying <em>both</em> Set-ref shapes on one blue
/// chassis; DataContext is a <see cref="SetRefVm"/>.
/// <para>
/// Visual target (strictly per <c>docs/silmarillion-visual-grammar.md</c> — every value
/// is a committed token, none invented here): a blue chip <em>with</em> a chassis
/// (unlike <see cref="Link"/>) — text <c>SetRefTextBrush</c>, idle fill
/// <c>SetRefIdleFillBrush</c>, 1px <c>SetRefBorderBrush</c> border, <c>GrammarRadiusSm</c>
/// corner, 1×8 padding. <b>summary-form</b> appends <c> · {count} →</c>; <b>tag-form</b>
/// shows only the label (no count, no arrow, no glyph). Optional leading
/// <c>SlotOrdinal</c> prefix in <c>TextQuaternaryBrush</c> for stacked positionally-
/// material slots. Hover darkens the fill toward <c>AccentSoftBrush</c> and brightens the
/// border (the "drawer-pulling" feel); <c>Cursor=Hand</c> only when actionable.
/// </para>
/// <para>
/// <b>Availability corollary (never-grey-pill guarantee):</b> when
/// <see cref="SetRefVm.IsActionable"/> is false the rest state is <em>visually
/// identical</em> — same blue chassis, same fill/border/text. It MUST NOT degrade to an
/// inert grey Fact pill (the forbidden inverted-affordance lie). Only interaction
/// differs: no hand cursor, and a click is a safe no-op
/// (<see cref="SetRefClickAction.Unavailable"/>).
/// </para>
/// Default style ships in <c>Mithril.Shared.Wpf/Resources.xaml</c> (appended after the
/// <see cref="Link"/> style, mirroring its templated-control pattern).
/// </summary>
public sealed class SetRef : Control
{
    static SetRef()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SetRef), new FrameworkPropertyMetadata(typeof(SetRef)));
    }

    /// <summary>
    /// Parent-supplied reveal/filter command. Bound on the control itself, not on the
    /// <see cref="SetRefVm"/> — the VM is a data carrier; the command is supplied by the
    /// hosting view. Receives the bound <see cref="SetRefVm"/> as its parameter (mirrors
    /// how <see cref="Link.ClickCommand"/> / <see cref="EntityChip.ClickCommand"/> pass
    /// their click param). Invoked only when the VM is actionable.
    /// </summary>
    public ICommand? ActivateCommand
    {
        get => (ICommand?)GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.Register(
        nameof(ActivateCommand),
        typeof(ICommand),
        typeof(SetRef),
        new PropertyMetadata(null));

    /// <summary>
    /// Pure decision: given a (possibly null) bound VM, what should a click do? Factored
    /// out of <see cref="OnClick"/> so the actionable-vs-unwired branch is unit-testable
    /// without the visual tree. Actionable → <see cref="SetRefClickAction.Activate"/>;
    /// any other non-null VM → <see cref="SetRefClickAction.Unavailable"/> (availability
    /// corollary: still a Set-ref, never a dead-end <em>and</em> never an invented UI —
    /// the safe no-op); null VM → <see cref="SetRefClickAction.None"/>.
    /// </summary>
    public static SetRefClickAction ResolveClick(SetRefVm? vm)
    {
        if (vm is null) return SetRefClickAction.None;
        return vm.IsActionable ? SetRefClickAction.Activate : SetRefClickAction.Unavailable;
    }

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
        var vm = DataContext as SetRefVm;
        switch (ResolveClick(vm))
        {
            case SetRefClickAction.Activate:
                var cmd = ActivateCommand;
                if (cmd is not null && cmd.CanExecute(vm))
                    cmd.Execute(vm);
                break;

            // Unavailable: availability-corollary no-op. The grammar specifies no
            // behaviour for an un-wired Set-ref click; do the minimal safe thing
            // (nothing — no command, no exception, no invented affordance).
            case SetRefClickAction.Unavailable:
            case SetRefClickAction.None:
            default:
                break;
        }
    }
}
