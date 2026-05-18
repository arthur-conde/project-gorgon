using Mithril.Shared.Wpf;

namespace Palantir.ViewModels;

/// <summary>
/// Composes Palantir's tabs as VM data so the View can bind
/// <c>TabControl.ItemsSource</c> rather than constructing TabItems in code.
/// Tab views are picked by <c>DataTemplate</c>s keyed on each child VM's type.
/// </summary>
public sealed class PalantirShellViewModel
{
    public IReadOnlyList<ModuleTab> Tabs { get; }

    public PalantirShellViewModel(
        LiveInventoryViewModel liveInventory,
        WorldStateViewModel worldState,
        NotificationTesterViewModel notificationTester)
    {
        Tabs = new[]
        {
            new ModuleTab("Live Inventory", liveInventory),
            new ModuleTab("World State", worldState),
            new ModuleTab("Notification Tester", notificationTester),
        };
    }
}
