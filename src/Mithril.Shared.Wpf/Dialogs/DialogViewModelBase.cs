using CommunityToolkit.Mvvm.ComponentModel;

namespace Mithril.Shared.Wpf.Dialogs;

public abstract class DialogViewModelBase : ObservableObject
{
    public abstract string Title { get; }

    public virtual string PrimaryButtonText => "Save";

    public virtual string? SecondaryButtonText => "Cancel";

    /// <summary>
    /// Caps the dialog host's outer width. The default suits prose-shaped dialogs
    /// (settings, confirms, share links). Dialogs that embed wide content — image
    /// previews, full-grid renders — override this so the host doesn't squeeze the
    /// inner content down to ~560 px regardless of what the inner control's own
    /// MaxWidth allows.
    /// </summary>
    public virtual double MaxContentWidth => 560;

    /// <summary>
    /// Called when the primary button is clicked. Return <c>true</c> to close the dialog,
    /// <c>false</c> to keep it open (e.g. validation failed).
    /// </summary>
    public virtual bool OnPrimaryAction() => true;

    public event Action<bool?>? CloseRequested;

    protected void RequestClose(bool? result) => CloseRequested?.Invoke(result);
}
