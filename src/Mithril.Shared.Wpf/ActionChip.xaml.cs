using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Ghost / outline variant of <see cref="EntityChip"/> for "open this filtered tab" shortcuts
/// on detail panes — e.g. the Area-detail "View all N in NPCs tab →" affordance. Binds to the
/// same <see cref="EntityChipVm"/> shape and exposes the same <see cref="ClickCommand"/>
/// dependency property, so call sites can swap one for the other without restructuring the
/// surrounding XAML.
/// <para>
/// The visual treatment is deliberately subordinate: transparent fill, gold border + foreground
/// when navigable, gray when not, leading <c>ListFilter</c> icon. The intent is for the user to
/// tell "this is a navigation action" apart from "this is an entity" at a glance — a flat-
/// equal chip strip mixing the two reads ambiguously.
/// </para>
/// </summary>
public partial class ActionChip : UserControl
{
    public ActionChip()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Parent-supplied navigation command. Mirrors <see cref="EntityChip.ClickCommand"/> —
    /// command parameter is the bound <see cref="EntityChipVm.Reference"/>, gated on
    /// <see cref="EntityChipVm.IsNavigable"/>.
    /// </summary>
    public ICommand? ClickCommand
    {
        get => (ICommand?)GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }

    public static readonly DependencyProperty ClickCommandProperty = DependencyProperty.Register(
        nameof(ClickCommand),
        typeof(ICommand),
        typeof(ActionChip),
        new PropertyMetadata(null));

    private void OnChipClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EntityChipVm vm || !vm.IsNavigable) return;
        var cmd = ClickCommand;
        if (cmd is null || !cmd.CanExecute(vm.Reference)) return;
        cmd.Execute(vm.Reference);
    }
}
