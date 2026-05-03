using System.Windows;
using System.Windows.Controls;
using Arwen.ViewModels;

namespace Arwen.Views;

public partial class FavorView : UserControl, IFavorViewNavigator
{
    public FavorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Adds a tab with its DataContext already set, and returns the created
    /// <see cref="TabItem"/> so callers can attach a notification badge via
    /// <c>Mithril.Shared.Wpf.TabBadge.SetCount(item, n)</c> or bind it to a VM
    /// property. Called by ArwenModule during DI registration so each tab
    /// has its ViewModel before any bindings are evaluated.
    /// </summary>
    public TabItem AddTab(string header, UserControl content)
    {
        var tab = new TabItem
        {
            Header = header,
            Content = content,
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
        };
        Tabs.Items.Add(tab);
        return tab;
    }

    /// <summary>
    /// Switches to the Gift Scanner tab and tells its VM to focus the given NPC.
    /// Silent no-op if the tab isn't registered or the NPC isn't in the dropdown
    /// (player hasn't met them yet).
    /// </summary>
    public void OpenInGiftScanner(string npcKey)
    {
        foreach (TabItem tab in Tabs.Items)
        {
            if (tab.Header is not string header || header != "Gift Scanner") continue;

            Tabs.SelectedItem = tab;

            if (tab.Content is FrameworkElement fe && fe.DataContext is GiftScannerViewModel vm)
            {
                var entry = vm.NpcList.FirstOrDefault(e => e.NpcKey == npcKey);
                if (entry is not null) vm.SelectedNpc = entry;
            }
            return;
        }
    }
}
