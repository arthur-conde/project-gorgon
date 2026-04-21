using CommunityToolkit.Mvvm.ComponentModel;

namespace Gorgon.Shared.Wpf.Dialogs;

public abstract class DialogViewModelBase : ObservableObject
{
    public abstract string Title { get; }

    public virtual string PrimaryButtonText => "Save";

    public virtual string? SecondaryButtonText => "Cancel";

    /// <summary>
    /// Called when the primary button is clicked. Return <c>true</c> to close the dialog,
    /// <c>false</c> to keep it open (e.g. validation failed).
    /// </summary>
    public virtual bool OnPrimaryAction() => true;

    public event Action<bool?>? CloseRequested;

    protected void RequestClose(bool? result) => CloseRequested?.Invoke(result);
}
