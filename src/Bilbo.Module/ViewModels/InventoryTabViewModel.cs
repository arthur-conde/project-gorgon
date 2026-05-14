using Bilbo.Domain;
using Mithril.Shared.Settings;
using Mithril.Shared.Wpf;

namespace Bilbo.ViewModels;

/// <summary>
/// Wraps the shared <see cref="StorageViewModel"/> for the Inventory tab and
/// carries the view-side services that <c>InventoryTab</c>'s code-behind needs
/// to wire its <c>MithrilDataGrid</c> column-state persistence and item-detail
/// double-click handler. Exposed as a distinct VM type so the parent view's
/// DataTemplate-by-type selector renders the Inventory view rather than the
/// Recipes view (both tabs share <see cref="Storage"/>).
/// </summary>
public sealed class InventoryTabViewModel
{
    public StorageViewModel Storage { get; }
    public BilboSettings Settings { get; }
    public SettingsAutoSaver<BilboSettings> Saver { get; }
    public IItemDetailPresenter ItemDetail { get; }

    public InventoryTabViewModel(
        StorageViewModel storage,
        BilboSettings settings,
        SettingsAutoSaver<BilboSettings> saver,
        IItemDetailPresenter itemDetail)
    {
        Storage = storage;
        Settings = settings;
        Saver = saver;
        ItemDetail = itemDetail;
    }
}
