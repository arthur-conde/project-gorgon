using System.Windows;

namespace Gorgon.Shared.Wpf.Dialogs;

public interface IDialogService
{
    bool? ShowDialog(DialogViewModelBase viewModel, FrameworkElement content);
}
