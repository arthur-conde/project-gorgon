using System.Windows;
using System.Windows.Input;
using Gorgon.Shell.ViewModels;

namespace Gorgon.Shell.Views;

public partial class DiagnosticsView : System.Windows.Controls.UserControl
{
    public DiagnosticsView() { InitializeComponent(); }

    private void CategoryChip_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not CategoryToggle toggle) return;
        if (DataContext is not DiagnosticsViewModel vm) return;
        vm.CategoriesOnlyCommand.Execute(toggle);
        e.Handled = true;
    }
}
