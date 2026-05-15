using System.Windows;
using System.Windows.Input;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Shared 1:N provenance popup window (#318). Sibling to <see cref="EntityChip"/> /
/// <see cref="ActionChip"/>; binds a <see cref="ProvenancePopupViewModel"/> and renders
/// membership + provenance for a reverse-lookup relationship, fed the source index
/// collection directly.
/// <para>
/// <b>Non-navigating contract.</b> Opened via <see cref="Window.Show()"/> by the host
/// (e.g. a detail VM's "View all N" command) and never calls
/// <c>IReferenceNavigator.Open/Back/Forward</c>. Opening the popup therefore pushes no
/// back/forward history — identical to every <c>IReferenceKindTarget.TryOpenInWindow</c>
/// detail window, which also just <c>new XWindow{...}.Show()</c>s. Clicking a chip
/// <em>inside</em> the popup does navigate (a 1:1 entity ref via
/// <see cref="ChipClickCommand"/>) — the intended, history-pushing action, consistent
/// with the chip-vs-popup rule (the popup is composed of direct references).
/// </para>
/// </summary>
public partial class ProvenancePopupWindow : Window
{
    public ProvenancePopupWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Parent-supplied navigation command invoked when a chip inside the popup is clicked.
    /// Bound on the window (not the chip VM) so the host injects its own
    /// <c>OpenEntityCommand</c>; receives the chip's <see cref="EntityChipVm.Reference"/>.
    /// Mirrors <see cref="EntityChip.ClickCommand"/>'s contract.
    /// </summary>
    public ICommand? ChipClickCommand
    {
        get => (ICommand?)GetValue(ChipClickCommandProperty);
        set => SetValue(ChipClickCommandProperty, value);
    }

    public static readonly DependencyProperty ChipClickCommandProperty = DependencyProperty.Register(
        nameof(ChipClickCommand),
        typeof(ICommand),
        typeof(ProvenancePopupWindow),
        new PropertyMetadata(null));

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
