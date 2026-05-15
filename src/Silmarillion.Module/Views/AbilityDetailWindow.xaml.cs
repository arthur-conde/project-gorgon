using System.Windows;
using System.Windows.Input;

namespace Silmarillion.Views;

public partial class AbilityDetailWindow : Window
{
    public AbilityDetailWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
