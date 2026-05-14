using Mithril.Shared.Wpf;

namespace Bilbo.ViewModels;

/// <summary>
/// Composes Bilbo's tabs as VM data so the View can bind
/// <c>TabControl.ItemsSource</c> rather than hard-coding TabItems in XAML.
/// Tab views are picked by <c>DataTemplate</c>s keyed on each tab VM's type.
/// <para>
/// Both tabs share the same <see cref="Storage"/> VM but render through
/// different views; the wrapper VMs (<see cref="InventoryTabViewModel"/> /
/// <see cref="CraftableRecipesTabViewModel"/>) exist solely so DataTemplate-
/// by-type can distinguish them. The shell exposes <see cref="Storage"/>
/// directly too so the surrounding chrome (refresh button, status bar) can
/// bind to it via <c>{Binding Storage.X}</c>.
/// </para>
/// </summary>
public sealed class BilboShellViewModel
{
    public StorageViewModel Storage { get; }
    public IReadOnlyList<ModuleTab> Tabs { get; }

    public BilboShellViewModel(
        StorageViewModel storage,
        InventoryTabViewModel inventory,
        CraftableRecipesTabViewModel recipes)
    {
        Storage = storage;
        Tabs = new[]
        {
            new ModuleTab("Inventory", inventory),
            new ModuleTab("Craftable Recipes", recipes),
        };
    }
}
