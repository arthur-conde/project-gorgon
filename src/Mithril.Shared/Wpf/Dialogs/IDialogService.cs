using System.Windows;

namespace Mithril.Shared.Wpf.Dialogs;

public interface IDialogService
{
    bool? ShowDialog(DialogViewModelBase viewModel, FrameworkElement content);
}
