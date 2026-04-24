namespace Gorgon.Shared.Wpf;

/// <summary>
/// Shell-level service that opens a non-modal <see cref="ItemDetailWindow"/> for a given
/// item internal name. Any module that references items (Celebrimbor recipe tooltips,
/// Bilbo inventory rows, future deep-link handlers, …) resolves this to present the same
/// consistent detail view without replicating the UI.
/// </summary>
public interface IItemDetailPresenter
{
    /// <summary>
    /// Opens the detail window for the item with the given internal name. If the item
    /// cannot be resolved in reference data, the call is a logged no-op rather than
    /// throwing — callers are UI event handlers that shouldn't have to guard against this.
    /// </summary>
    void Show(string internalName);
}
