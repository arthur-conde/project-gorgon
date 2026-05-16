namespace Mithril.Shared.Wpf.Dialogs;

/// <summary>
/// View model for the themed yes/no confirmation rendered through
/// <see cref="DialogWindow"/> (the app's custom chrome) instead of a raw
/// <c>MessageBox</c>. Primary = confirm, secondary = cancel; the window's
/// default <see cref="DialogViewModelBase.OnPrimaryAction"/> (returns true)
/// is exactly the "Yes ⇒ true" semantics <see cref="IDialogService.Confirm"/>
/// needs.
/// </summary>
public sealed class ConfirmDialogViewModel : DialogViewModelBase
{
    public ConfirmDialogViewModel(string title, string message,
        string primaryButtonText = "Yes", string secondaryButtonText = "Cancel")
    {
        Title = title;
        Message = message;
        PrimaryButtonText = primaryButtonText;
        SecondaryButtonText = secondaryButtonText;
    }

    public override string Title { get; }
    public string Message { get; }
    public override string PrimaryButtonText { get; }
    public override string? SecondaryButtonText { get; }
}
