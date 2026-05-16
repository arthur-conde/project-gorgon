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
        // Themed chrome (DialogWindow) — consistent with the rest of the app
        // rather than a raw OS MessageBox. Primary ⇒ true; secondary / X ⇒ false.
        => ShowDialog(new ConfirmDialogViewModel(title, message), new ConfirmDialogView()) == true;
}
