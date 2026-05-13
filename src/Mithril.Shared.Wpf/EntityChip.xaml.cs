using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Shared cross-link chip. Binds to an <see cref="EntityChipVm"/> DataContext and renders
/// as a clickable accent-styled button when <see cref="EntityChipVm.IsNavigable"/> is true,
/// or as a disabled plain chip otherwise. Click invokes the parent-supplied
/// <see cref="ClickCommand"/> with the chip's <see cref="EntityChipVm.Reference"/> as
/// command parameter.
/// </summary>
public partial class EntityChip : UserControl
{
    public EntityChip()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Parent-supplied navigation command. Bound on the chip control itself, not on the
    /// EntityChipVm — the VM is a data carrier, the command is supplied by the hosting view.
    /// Receives the chip's <see cref="EntityChipVm.Reference"/> as its parameter.
    /// </summary>
    public ICommand? ClickCommand
    {
        get => (ICommand?)GetValue(ClickCommandProperty);
        set => SetValue(ClickCommandProperty, value);
    }

    public static readonly DependencyProperty ClickCommandProperty = DependencyProperty.Register(
        nameof(ClickCommand),
        typeof(ICommand),
        typeof(EntityChip),
        new PropertyMetadata(null));
}
