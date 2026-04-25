using Mithril.Shared.Reference;

namespace Mithril.Shared.Wpf;

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

    /// <summary>
    /// Opens the detail window with a pre-rendered <paramref name="augments"/> list
    /// surfaced as an "Augmentation" section. Used by Celebrimbor for
    /// <c>AddItemTSysPower</c> recipes that attach a power tier to the base item.
    /// Passing an empty list behaves like <see cref="Show(string)"/>.
    /// </summary>
    void Show(string internalName, IReadOnlyList<AugmentPreview> augments);

    /// <summary>
    /// Opens the detail window with a full <see cref="ItemDetailContext"/> bundle of
    /// pre-rendered previews. Used by Celebrimbor recipe rows that need to surface
    /// multiple Phase 7 sections (Infusions, Possible augments, Teaches, Additional
    /// effects) alongside the base augmentation chip.
    /// </summary>
    void Show(string internalName, ItemDetailContext context);
}
