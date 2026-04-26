using System.Windows;
using System.Windows.Input;

namespace Mithril.Shared.Wpf;

public partial class IngredientSourcesWindow : Window
{
    public IngredientSourcesWindow(IngredientSourcesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
