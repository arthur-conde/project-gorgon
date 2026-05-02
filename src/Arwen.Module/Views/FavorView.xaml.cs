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
    /// Adds a tab with its DataContext already set.
    /// Called by ArwenModule during DI registration so each tab
    /// has its ViewModel before any bindings are evaluated.
    /// </summary>
    public void AddTab(string header, UserControl content)
    {
        Tabs.Items.Add(new TabItem
        {
            Header = header,
            Content = content,
            Margin = new System.Windows.Thickness(0, 8, 0, 0),
        });
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
