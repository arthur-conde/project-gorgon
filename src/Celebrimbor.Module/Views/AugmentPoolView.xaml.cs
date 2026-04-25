using System.Windows;
using Celebrimbor.ViewModels;

namespace Celebrimbor.Views;

public partial class AugmentPoolView : Window
{
    public AugmentPoolView(AugmentPoolViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
