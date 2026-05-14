using Mithril.Shared.Wpf;

namespace Smaug.ViewModels;

/// <summary>
/// Composes Smaug's tabs as VM data so the View can bind
/// <c>TabControl.ItemsSource</c> rather than constructing TabItems in code.
/// Tab views are picked by <c>DataTemplate</c>s keyed on each child VM's type.
/// </summary>
public sealed class SmaugShellViewModel
{
    public IReadOnlyList<ModuleTab> Tabs { get; }

    public SmaugShellViewModel(
        VendorShopViewModel vendorShop,
        StorageSellbackViewModel storageSellback,
        SellPlannerViewModel sellPlanner,
        VendorCatalogViewModel vendorCatalog,
        SellPricesViewModel sellPrices,
        CalibrationViewModel calibration)
    {
        Tabs = new[]
        {
            new ModuleTab("Vendor Shop", vendorShop),
            new ModuleTab("Storage Sellback", storageSellback),
            new ModuleTab("Sell Planner", sellPlanner),
            new ModuleTab("Vendor Catalog", vendorCatalog),
            new ModuleTab("Sell Prices", sellPrices),
            new ModuleTab("Calibration", calibration),
        };
    }
}
