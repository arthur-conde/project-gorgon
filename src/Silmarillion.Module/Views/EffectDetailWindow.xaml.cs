using System.Windows;
using System.Windows.Input;

namespace Silmarillion.Views;

public partial class EffectDetailWindow : Window
{
    public EffectDetailWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
