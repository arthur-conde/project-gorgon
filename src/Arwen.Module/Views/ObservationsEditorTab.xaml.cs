using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Arwen.ViewModels;

namespace Arwen.Views;

public partial class ObservationsEditorTab : UserControl
{
    public ObservationsEditorTab()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Keyboard shortcuts for the Quantity TextBox: Enter commits, Escape reverts.
    /// LostFocus does NOT auto-commit — accidental tab-out shouldn't persist an edit.
    /// </summary>
    private void QuantityTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ObservationRow row) return;
        if (e.Key == Key.Enter)
        {
            if (DataContext is CalibrationViewModel vm && vm.CommitQuantityCommand.CanExecute(row))
                vm.CommitQuantityCommand.Execute(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.RevertQuantity();
            e.Handled = true;
        }
    }

    private void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ObservationRow row)
            row.RevertQuantity();
    }
}
