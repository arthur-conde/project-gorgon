using System.Windows.Controls;
using System.Windows.Input;
using Silmarillion.ViewModels;

namespace Silmarillion.Views;

public partial class SilmarillionView : UserControl
{
    public SilmarillionView()
    {
        InitializeComponent();
        // Mouse extended buttons can't be expressed as a XAML MouseBinding cleanly; handled here.
        PreviewMouseDown += OnPreviewMouseDown;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SilmarillionViewModel vm) return;

        if (e.ChangedButton == MouseButton.XButton1 && vm.BackCommand.CanExecute(null))
        {
            vm.BackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2 && vm.ForwardCommand.CanExecute(null))
        {
            vm.ForwardCommand.Execute(null);
            e.Handled = true;
        }
    }
}
