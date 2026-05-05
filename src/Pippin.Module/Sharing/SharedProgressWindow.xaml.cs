using System.Windows;

namespace Pippin.Sharing;

public partial class SharedProgressWindow : Window
{
    public SharedProgressWindow(SharedProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
