using System.Windows;

namespace Mithril.Shared.Wpf.Dialogs;

public interface IDialogService
{
    bool? ShowDialog(DialogViewModelBase viewModel, FrameworkElement content);

    /// <summary>
    /// Modal yes/no confirmation. Returns true when the user explicitly confirms
    /// (clicks Yes / OK), false on No / Cancel / dismiss. Use for destructive
    /// actions that need an "are you sure?" gate.
    /// </summary>
    bool Confirm(string title, string message);
}
