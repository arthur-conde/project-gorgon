using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Shared chip for an item / recipe source row. Binds to an <see cref="ItemSourceChipVm"/>
/// DataContext and renders as a clickable accent-styled button when
/// <see cref="ItemSourceChipVm.IsNavigable"/> is true, or as plain text in a transparent
/// frame when not — preserving the legacy "Vendor: NPC_X" plain rendering for source kinds
/// without a tab yet. Mirrors <see cref="EntityChip"/>'s shape but reads the parallel
/// <see cref="ItemSourceChipVm"/> with its nullable <see cref="ItemSourceChipVm.EntityReference"/>.
/// </summary>
public partial class ItemSourceChip : UserControl
{
    public ItemSourceChip()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Parent-supplied navigation command. Receives the chip's
    /// <see cref="ItemSourceChipVm.EntityReference"/> as parameter.
    /// </summary>
    public ICommand? ClickCommand
    {
        get => (ICommand?)GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }

    public static readonly DependencyProperty ClickCommandProperty = DependencyProperty.Register(
        nameof(ClickCommand),
        typeof(ICommand),
        typeof(ItemSourceChip),
        new PropertyMetadata(null));

    private void OnChipClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ItemSourceChipVm vm || !vm.IsNavigable || vm.EntityReference is null) return;
        var cmd = ClickCommand;
        if (cmd is null || !cmd.CanExecute(vm.EntityReference)) return;
        cmd.Execute(vm.EntityReference);
    }
}
