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
}
