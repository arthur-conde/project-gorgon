using System.Windows;

namespace Mithril.Shared.Wpf.Dialogs;

public sealed class DialogService : IDialogService
{
    public bool? ShowDialog(DialogViewModelBase viewModel, FrameworkElement content)
    {
        var window = new DialogWindow(viewModel, content)
        {
            Owner = Application.Current.MainWindow,
        };
        return window.ShowDialog();
    }

    public bool Confirm(string title, string message)
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}
